using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BatchImageLoaderLibrary
{
	public class BatchImageLoader
	{
		private static readonly object LockObject = new();
		private static BatchImageLoader? instance;
		private static int imagesInQueue = 0;
		private static ConcurrentDictionary<string, Lazy<Task<CachedImage>>> Images = new();
		private static int threadsCount = 0;
		private static StorageFacade storage = null!;
		private static int imagesLoading = 0;

		// Пути кэша: абсолютные, по умолчанию рядом с исполняемым файлом (а не
		// относительно текущего каталога процесса, который меняют OpenFileDialog,
		// ярлыки и планировщик). Можно задать до первого обращения к Instance
		// или позже — тогда провайдер пересоздаётся.
		private static string cacheDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "cache");
		private static string databasePath = System.IO.Path.Combine(AppContext.BaseDirectory, "BatchImageLoaderLibraryCache.sqlite");

		// Жёсткий лимит параллелизма загрузок. Создаётся лениво из текущего
		// MaxThreadsCount при первой загрузке, поэтому значение нужно задать
		// ДО первого GetImageFromUrl (так его и используют все потребители).
		private static SemaphoreSlim? throttle;
		private static readonly object throttleLock = new();

		// Сколько раз пробуем установить соединение и максимум на ОДНУ попытку.
		// Свой дедлайн на попытку нужен, чтобы единичный «зависший» SYN не сжёг весь
		// 30-секундный бюджет запроса и не вылез наружу как "The operation was canceled".
		private const int ConnectAttempts = 3;
		private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(8);

		// Привязка исходящего сокета к локальному порту вне зарезервированных WinNAT диапазонов
		// (Hyper-V/WSL/Docker), иначе connect падает с SocketException 10013 (WSAEACCES).
		private static async ValueTask<Stream> ConnectViaSafeLocalPort(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
		{
			Exception? lastError = null;
			for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
			{
				// Если запрос уже отменён снаружи (закрыли форму / истёк общий таймаут) — выходим сразу.
				cancellationToken.ThrowIfCancellationRequested();

				Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
				// Отдельный дедлайн на попытку, связанный с внешней отменой.
				using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				attemptCts.CancelAfter(ConnectAttemptTimeout);
				try
				{
					BindToSafeLocalPort(socket);
					if (FileLog.Enabled)
						FileLog.Write("connect: " + context.DnsEndPoint.Host + ":" + context.DnsEndPoint.Port +
							" attempt " + attempt + "/" + ConnectAttempts + " from " + socket.LocalEndPoint);
					await socket.ConnectAsync(context.DnsEndPoint, attemptCts.Token).ConfigureAwait(false);
					FileLog.Write("conn-ok: " + context.DnsEndPoint.Host + " via " + socket.LocalEndPoint + " (attempt " + attempt + ")");
					return new NetworkStream(socket, ownsSocket: true);
				}
				catch (Exception ex)
				{
					socket.Dispose();
					FileLog.Write("conn-x : " + context.DnsEndPoint.Host + " attempt " + attempt + ": " +
						ex.GetType().Name + ": " + ex.Message);

					// Настоящая отмена запроса (а не наш per-attempt таймаут) — пробрасываем,
					// чтобы HttpClient корректно отчитался об отмене/таймауте.
					if (cancellationToken.IsCancellationRequested)
						throw new OperationCanceledException("Загрузка изображения отменена.", ex, cancellationToken);

					// Иначе это таймаут попытки или транзиентный сбой сокета (10013 и т.п.) — пробуем ещё раз.
					lastError = ex;
				}
			}

			// Все попытки исчерпаны: внятная ошибка вместо «The operation was canceled».
			// Выше по стеку LoadImage её залогирует и подставит заглушку 404.
			throw new HttpRequestException($"Не удалось подключиться к {context.DnsEndPoint.Host} за {ConnectAttempts} попыток.", lastError);
		}

		private static void BindToSafeLocalPort(Socket socket)
		{
			// Пользовательский диапазон 20000..48999 — ниже эфемерного (49152+), где сидят резервы WinNAT.
			for (int i = 0; i < 64; i++)
			{
				try { socket.Bind(new IPEndPoint(IPAddress.Any, 20000 + Random.Shared.Next(0, 29000))); return; }
				catch (SocketException) { /* порт занят — берём другой */ }
			}
			socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // крайний случай — пусть выбирает ОС
		}

		// Один разделяемый HttpClient на всю библиотеку: переиспользует пул
		// соединений и не плодит сокеты в TIME_WAIT при пакетной загрузке.
		// Создаётся лениво при первой загрузке, чтобы успеть подменить транспорт.
		private static HttpClient? httpClient;
		private static readonly object httpLock = new();

		// Подменяемый транспорт: задать ДО первой загрузки. null — стандартный
		// SocketsHttpHandler с привязкой локального порта. Нужен тестам (фейковый
		// handler) и нестандартным сценариям: прокси, заголовки через DelegatingHandler.
		public static HttpMessageHandler? HttpHandler { get; set; }

		// Общий таймаут на один запрос: соединение + заголовки + чтение тела.
		public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

		// Лимит размера ответа. Больше — сбой загрузки (заглушка без кэширования),
		// чтобы случайный или вредоносный URL не съел память процесса.
		public static long MaxImageBytes { get; set; } = 64L * 1024 * 1024;

		private static HttpClient GetHttpClient()
		{
			HttpClient? client = httpClient;
			if (client != null)
				return client;

			lock (httpLock)
			{
				// PooledConnectionLifetime заставляет периодически пересоздавать
				// соединения, чтобы подхватывать изменения DNS у вечного клиента.
				return httpClient ??= new HttpClient(HttpHandler ?? new SocketsHttpHandler
				{
					PooledConnectionLifetime = TimeSpan.FromMinutes(5),
					ConnectCallback = ConnectViaSafeLocalPort
				})
				{
					// Таймаут держим сами (CTS на запрос в LoadImage): встроенный
					// не покрывает чтение тела при ResponseHeadersRead.
					Timeout = System.Threading.Timeout.InfiniteTimeSpan
				};
			}
		}

		// Картинка-заглушка встроена в сборку (EmbeddedResource 404.png),
		// чтобы не таскать файл рядом с exe. Загружается один раз.
		private static readonly byte[] NotFoundImage = LoadNotFoundImage();

		private static byte[] LoadNotFoundImage()
		{
			try
			{
				Assembly assembly = Assembly.GetExecutingAssembly();
				string? resourceName = Array.Find(
					assembly.GetManifestResourceNames(),
					n => n.EndsWith("404.png", StringComparison.OrdinalIgnoreCase));

				if (resourceName == null)
					return Array.Empty<byte>();

				using Stream? stream = assembly.GetManifestResourceStream(resourceName);
				if (stream == null)
					return Array.Empty<byte>();

				using MemoryStream ms = new MemoryStream();
				stream.CopyTo(ms);
				return ms.ToArray();
			}
			catch
			{
				return Array.Empty<byte>();
			}
		}


		public int ImagesLoaded => Images.Count;

		public int ThreadCount => threadsCount;

		public int MaxThreadsCount { get; set; } = 64;

		public int ImagesLoading => imagesLoading;

		public int ImagesProcessing => imagesInQueue + ImagesLoading;

		public int ImagesInQueue => imagesInQueue;

		public bool CreateThumbnails { get; set; } = true;

		public int ThumbnailWidth { get; set; } = 120;

		public int ThumbnailHeigth { get; set; } = 120;

		public bool NeedSaveToCache { get; set; } = true;

		private static StorageType storageType = StorageType.DB;

		public static StorageType StorageType
		{
			get => storageType;
			set
			{
				storageType = value;
				// Если хранилище уже создано — переключаем его на лету; иначе
				// значение подхватит конструктор при первом обращении к Instance.
				if (storage != null)
					storage.StorageType = value;
			}
		}

		// Каталог файлового кэша (StorageType.FILE). Относительный путь
		// разрешается один раз, в момент присваивания.
		public static string CacheDirectory
		{
			get => cacheDirectory;
			set
			{
				cacheDirectory = System.IO.Path.GetFullPath(value);
				if (storage != null)
					storage.CacheDirectory = cacheDirectory;
			}
		}

		// Путь к файлу SQLite-кэша (StorageType.DB).
		public static string DatabasePath
		{
			get => databasePath;
			set
			{
				databasePath = System.IO.Path.GetFullPath(value);
				if (storage != null)
					storage.DatabasePath = databasePath;
			}
		}

		// Путь к файлу детального лога. null/пусто = логирование ВЫКЛЮЧЕНО
		// (по умолчанию). Установка пути включает подробную запись всех
		// операций в этот файл (дозапись). Только для диагностики.
		public static string? LogFile
		{
			get => FileLog.Path;
			set => FileLog.Configure(value);
		}



		private BatchImageLoader()
		{
			storage = new StorageFacade(StorageType, cacheDirectory, databasePath);
		}

		public static BatchImageLoader Instance
		{
			get
			{
				lock (LockObject)
				{
					instance ??= new BatchImageLoader();
				}
				return instance;
			}
		}

		public Task<CachedImage> GetImageFromUrl(string url)
		{
			if (FileLog.Enabled)
				FileLog.Write("request : " + url + " (known=" + Images.ContainsKey(url) + ")");
			// Один и тот же Task на каждый url: первый вызов запускает загрузку,
			// остальные (текущие и будущие) ждут его же — без поллинга и без
			// риска зависнуть; результат ИЛИ исключение получают все awaiter'ы.
			// Task.Run: весь конвейер (чтение кэша, HTTP, превью, запись) идёт на
			// пуле потоков, а не в потоке вызывающего и не на его
			// SynchronizationContext — UI-поток WinForms/WPF не блокируется.
			return Images.GetOrAdd(url, u =>
			{
				Lazy<Task<CachedImage>> entry = null!;
				entry = new Lazy<Task<CachedImage>>(() => Task.Run(() => LoadAsync(u, entry)));
				return entry;
			}).Value;
		}

		private async Task<CachedImage> LoadAsync(string url, Lazy<Task<CachedImage>> entry)
		{
			try
			{
				CachedImage result = await ProcessUrlAsync(url).ConfigureAwait(false);
				// Заглушку в памяти не держим: текущие ожидающие получат её из этого
				// Task, а следующий GetImageFromUrl запустит загрузку заново.
				if (result.IsPlaceholder)
					Forget(url, entry);
				return result;
			}
			catch (Exception ex)
			{
				// Не оставляем сбойную задачу в кэше — даём шанс повторить загрузку.
				FileLog.Write("evict  : " + url + " after error " + ex.GetType().Name + ": " + ex.Message);
				Forget(url, entry);
				throw;
			}
		}

		// Убирает из словаря именно СВОЮ запись: если её уже вытеснил ClearCache
		// и на её месте идёт новая загрузка, чужую запись не трогаем.
		private static void Forget(string url, Lazy<Task<CachedImage>> entry)
		{
			Images.TryRemove(new KeyValuePair<string, Lazy<Task<CachedImage>>>(url, entry));
		}

		private async Task<CachedImage> ProcessUrlAsync(string url)
		{
			Interlocked.Increment(ref imagesInQueue);
			FileLog.Write("queue+ : " + url + " (waiting=" + imagesInQueue + ", threads=" + threadsCount + ")");

			// Жёсткий лимит параллелизма: семафор не протекает (в отличие от
			// прежней проверки threadsCount ДО инкремента) и не крутит busy-wait.
			SemaphoreSlim throttle = GetThrottle();
			Stopwatch waitSw = Stopwatch.StartNew();
			await throttle.WaitAsync().ConfigureAwait(false);
			waitSw.Stop();
			Interlocked.Increment(ref threadsCount);
			Interlocked.Decrement(ref imagesInQueue);
			FileLog.Write("slot   : " + url + " (waited=" + waitSw.ElapsedMilliseconds + "ms, threads=" + threadsCount + ")");

			Stopwatch sw = Stopwatch.StartNew();
			try
			{
				// Размер превью (или orig) входит в ключ кэша. Вариант фиксируем
				// один раз на запрос и передаём явно — никакого общего состояния.
				string variant = CurrentVariant();
				bool loadFailed = false;
				byte[]? data = LoadFromCache(url, variant);
				if (data == null)
				{
					data = await LoadImage(url).ConfigureAwait(false);
					if (data == null || data.Length == 0)
					{
						loadFailed = true;
						data = NotFoundImage;
						FileLog.Write("dl-fail: " + url + " -> 404 placeholder (" + data.Length + " bytes)");
					}
					else
					{
						if (CreateThumbnails)
						{
							int before = data.Length;
							data = CreateThumbnail(data, ThumbnailHeigth, ThumbnailWidth);
							FileLog.Write("thumb  : " + url + " " + before + " -> " + (data?.Length ?? 0) +
								" bytes (" + variant + ")");
						}

						if (data == null)
						{
							loadFailed = true;
							data = NotFoundImage;
							FileLog.Write("thumb-x: " + url + " -> 404 placeholder");
						}
					}

					// Не кэшируем заглушку 404: иначе временный сбой сети навсегда
					// «отравит» URL — на следующих запусках вернётся 404 без ретрая.
					if (NeedSaveToCache && !loadFailed)
					{
						SaveToCache(url, variant, data);
						FileLog.Write("cached : " + url + " (" + data.Length + " bytes, variant=" + variant + ")");
					}
				}

				sw.Stop();
				FileLog.Write("done   : " + url + " (total=" + sw.ElapsedMilliseconds + "ms, " + data.Length + " bytes" +
					(loadFailed ? ", placeholder" : "") + ")");
				return new CachedImage(data, loadFailed);
			}
			finally
			{
				Interlocked.Decrement(ref threadsCount);
				throttle.Release();
			}
		}

		public static byte[]? CreateThumbnail(byte[] image, int h = 120, int w = 120)
		{
			try
			{
				using MemoryStream ms = new MemoryStream(image, 0, image.Length);
				using Image img = Image.FromStream(ms);
				using Bitmap b = new Bitmap(img, new Size(w, h));
				using MemoryStream ms2 = new MemoryStream();
				b.Save(ms2, System.Drawing.Imaging.ImageFormat.Jpeg);
				return ms2.ToArray();
			}
			catch (Exception e)
			{
				FileLog.Write("thumb-x: " + e.GetType().Name + ": " + e.Message);
				Trace.WriteLine("CreateThumbnail failed: " + e.Message);
				return null;
			}
		}

		// null = сбой загрузки: сеть, не-2xx, превышен лимит размера, тело — не
		// картинка. Выше это превращается в заглушку 404 без записи в кэш.
		private static async Task<byte[]?> LoadImage(string url)
		{
			FileLog.Write("http   : GET " + url);
			Interlocked.Increment(ref imagesLoading);
			Stopwatch sw = Stopwatch.StartNew();
			try
			{
				using CancellationTokenSource cts = new CancellationTokenSource(RequestTimeout);
				using HttpResponseMessage response = await GetHttpClient()
					.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();

				long limit = MaxImageBytes;
				long? declared = response.Content.Headers.ContentLength;
				if (declared > limit)
				{
					FileLog.Write("http-x : " + url + " too big: Content-Length " + declared + " > " + limit);
					return null;
				}

				// Читаем потоком с контролем размера: Content-Length может отсутствовать или врать.
				using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
				using MemoryStream ms = new MemoryStream(declared.HasValue ? (int)declared.Value : 64 * 1024);
				byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
				try
				{
					int read;
					while ((read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
					{
						if (ms.Length + read > limit)
						{
							FileLog.Write("http-x : " + url + " too big: body exceeds " + limit + " bytes");
							return null;
						}
						ms.Write(buffer, 0, read);
					}
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(buffer);
				}

				byte[] bytes = ms.ToArray();

				// 200 OK с HTML (капча, captive-portal, страница логина после
				// редиректа) — не картинка; в кэш такое попадать не должно.
				if (!ImageSignature.IsImage(bytes))
				{
					FileLog.Write("http-x : " + url + " not an image (" + bytes.Length + " bytes, Content-Type: " +
						(response.Content.Headers.ContentType?.MediaType ?? "-") + ")");
					return null;
				}

				FileLog.Write("http-ok: " + url + " -> " + bytes.Length + " bytes in " + sw.ElapsedMilliseconds + "ms");
				return bytes;
			}
			catch (Exception e)
			{
				FileLog.Write("http-x : " + url + " FAILED in " + sw.ElapsedMilliseconds + "ms: " +
					e.GetType().Name + ": " + e.Message);
				Trace.WriteLine("LoadImage failed for " + url + ": " + e.Message);
				return null;
			}
			finally
			{
				Interlocked.Decrement(ref imagesLoading);
			}
		}

		public async Task LoadFromCache()
		{
			// Предзагрузка идёт под текущим вариантом: другие размеры того же
			// URL в память не попадают.
			string variant = CurrentVariant();
			FileLog.Write("preload: reading cache (variant=" + variant + ", storage=" + StorageType + ")");
			// Чтение всего кэша — дисковая работа; уводим её с потока вызывающего.
			Dictionary<string, byte[]> data = await Task.Run(() => storage.GetAll(variant)).ConfigureAwait(false);
			foreach ((string key, byte[] value) in data)
			{
				Images.TryAdd(key, new Lazy<Task<CachedImage>>(Task.FromResult(new CachedImage(value))));
			}
			FileLog.Write("preload: " + data.Count + " images loaded into memory");
		}

		private static byte[]? LoadFromCache(string url, string variant)
		{
			byte[]? result = storage.Get(url, variant);
			if (FileLog.Enabled)
				FileLog.Write("cache  : " + url + " -> " + (result != null ? "HIT " + result.Length + " bytes" : "miss"));
			return result;
		}

		private static void SaveToCache(string url, string variant, byte[] data)
		{
			storage.Add(url, variant, data);
		}

		// Суффикс имени файла кэша: размер превью ("120x120") или "orig"
		// для полноразмерной картинки, когда генерация превью выключена.
		private string CurrentVariant()
		{
			return CreateThumbnails ? ThumbnailWidth + "x" + ThumbnailHeigth : "orig";
		}

		private SemaphoreSlim GetThrottle()
		{
			if (throttle != null)
				return throttle;
			lock (throttleLock)
			{
				int max = Math.Max(1, MaxThreadsCount);
				return throttle ??= new SemaphoreSlim(max, max);
			}
		}

		// Хранилище создаётся вместе с Instance, а статическим Clear* оно нужно
		// и до первого обращения к Instance.
		private static StorageFacade Storage
		{
			get
			{
				if (storage == null)
					_ = Instance;
				return storage!;
			}
		}

		// Забывает URL и в памяти, и на диске (все варианты): следующий
		// GetImageFromUrl загрузит его заново.
		public static void ClearCacheForUrl(string url)
		{
			FileLog.Write("clear  : " + url);
			Images.TryRemove(url, out _);
			Storage.Remove(url);
		}

		// Полная очистка: и in-memory словарь, и персистентный кэш.
		public static void ClearCache()
		{
			FileLog.Write("clear  : ALL");
			Images.Clear();
			Storage.RemoveAll();
		}

		// Встроенная заглушка 404 — тестам, чтобы сравнивать и проверять декодер.
		internal static byte[] PlaceholderBytes => NotFoundImage;

		// Только для тестов: полный сброс статического состояния синглтона,
		// чтобы каждый тест начинал с чистого загрузчика и своих путей.
		internal static void ResetForTests()
		{
			lock (LockObject)
			{
				Images.Clear();
				instance = null;
				storage = null!;
				throttle?.Dispose();
				throttle = null;
				httpClient?.Dispose();
				httpClient = null;
				HttpHandler = null;
				RequestTimeout = TimeSpan.FromSeconds(30);
				MaxImageBytes = 64L * 1024 * 1024;
				storageType = StorageType.DB;
				imagesInQueue = 0;
				threadsCount = 0;
				imagesLoading = 0;
				// Пул SQLite держит файл базы открытым — иначе временный каталог не удалить.
				Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			}
		}
	}
}
