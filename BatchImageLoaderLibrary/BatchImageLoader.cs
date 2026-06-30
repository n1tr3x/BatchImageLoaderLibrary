using System;
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
		// PooledConnectionLifetime заставляет периодически пересоздавать
		// соединения, чтобы подхватывать изменения DNS у вечного клиента.
		private static readonly HttpClient httpClient = new HttpClient(
			new SocketsHttpHandler
			{
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				ConnectCallback = ConnectViaSafeLocalPort
			})
		{
			Timeout = TimeSpan.FromSeconds(30)
		};

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
			storage = new StorageFacade(StorageType);
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

		/*
		public static CachedImage GetThumbnail(string path, int width, int height)
        {
            try
            {
                string filename = GetImagePath(path) + "_" + width + "x" + height + ".jpg";
                if (File.Exists(filename))
                    return new CachedImage(File.ReadAllBytes(filename));

                if (!Directory.Exists("cache"))
                    Directory.CreateDirectory("cache");

                using (Image img = Image.FromFile(path))
                {
                    using (Bitmap b = new Bitmap(img, new Size(width, height)))
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            b.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                            File.WriteAllBytes(GetImagePath(path) + "_" + width + "x" + height + ".jpg", ms.ToArray());
                            return new CachedImage(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return null;
            }
        }*/

		public Task<CachedImage> GetImageFromUrl(string url)
		{
			if (FileLog.Enabled)
				FileLog.Write("request : " + url + " (known=" + Images.ContainsKey(url) + ")");
			// Один и тот же Task на каждый url: первый вызов запускает загрузку,
			// остальные (текущие и будущие) ждут его же — без поллинга и без
			// риска зависнуть; результат ИЛИ исключение получают все awaiter'ы.
			return Images.GetOrAdd(url, u => new Lazy<Task<CachedImage>>(() => LoadAsync(u))).Value;
		}

		private async Task<CachedImage> LoadAsync(string url)
		{
			try
			{
				return await ProcessUrlAsync(url);
			}
			catch (Exception ex)
			{
				// Не оставляем сбойную задачу в кэше — даём шанс повторить загрузку.
				FileLog.Write("evict  : " + url + " after error " + ex.GetType().Name + ": " + ex.Message);
				Images.TryRemove(url, out _);
				throw;
			}
		}

        private async Task<CachedImage> ProcessUrlAsync(string url)
		{
			Interlocked.Increment(ref imagesInQueue);
			FileLog.Write("queue+ : " + url + " (waiting=" + imagesInQueue + ", threads=" + threadsCount + ")");

			// Жёсткий лимит параллелизма: семафор не протекает (в отличие от
			// прежней проверки threadsCount ДО инкремента) и не крутит busy-wait.
			SemaphoreSlim throttle = GetThrottle();
			Stopwatch waitSw = Stopwatch.StartNew();
			await throttle.WaitAsync();
			waitSw.Stop();
			Interlocked.Increment(ref threadsCount);
			Interlocked.Decrement(ref imagesInQueue);
			FileLog.Write("slot   : " + url + " (waited=" + waitSw.ElapsedMilliseconds + "ms, threads=" + threadsCount + ")");

			Stopwatch sw = Stopwatch.StartNew();
			try
			{
				// Размер превью (или orig) входит в ключ кэша, поэтому и чтение,
				// и запись должны идти под одним вариантом.
				storage.Variant = CurrentVariant();
				byte[]? data = LoadFromCache(url);
				if (data == null)
				{
					data = await LoadImage(url);
					bool loadFailed = false;
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
								" bytes (" + CurrentVariant() + ")");
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
						SaveToCache(url, data);
						FileLog.Write("cached : " + url + " (" + data.Length + " bytes, variant=" + CurrentVariant() + ")");
					}
				}

				sw.Stop();
				FileLog.Write("done   : " + url + " (total=" + sw.ElapsedMilliseconds + "ms, " + data.Length + " bytes)");
				return new CachedImage(data);
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

		private static async Task<byte[]?> LoadImage(string url)
		{
			FileLog.Write("http   : GET " + url);
			Interlocked.Increment(ref imagesLoading);
			Stopwatch sw = Stopwatch.StartNew();
			byte[]? bytes = null;
			try
			{
				bytes = await httpClient.GetByteArrayAsync(url);
				FileLog.Write("http-ok: " + url + " -> " + bytes.Length + " bytes in " + sw.ElapsedMilliseconds + "ms");
			}
			catch (Exception e)
			{
				FileLog.Write("http-x : " + url + " FAILED in " + sw.ElapsedMilliseconds + "ms: " +
					e.GetType().Name + ": " + e.Message);
				Trace.WriteLine("LoadImage failed for " + url + ": " + e.Message);
			}

			Interlocked.Decrement(ref imagesLoading);
			return bytes;
		}

		public async Task LoadFromCache()
		{
			// Предзагрузка тоже должна идти под текущим вариантом, иначе
			// GetAll вернёт картинки не того размера (или ничего).
			storage.Variant = CurrentVariant();
			FileLog.Write("preload: reading cache (variant=" + CurrentVariant() + ", storage=" + StorageType + ")");
			Dictionary<string, byte[]> data = await storage.GetAll();
			foreach ((string key, byte[] value) in data)
			{
				Images.TryAdd(key, new Lazy<Task<CachedImage>>(Task.FromResult(new CachedImage(value))));
			}
			FileLog.Write("preload: " + data.Count + " images loaded into memory");
		}

		private static byte[]? LoadFromCache(string url)
		{
			byte[]? result = storage.Get(url);
			if (FileLog.Enabled)
				FileLog.Write("cache  : " + url + " -> " + (result != null ? "HIT " + result.Length + " bytes" : "miss"));
			return result;
		}

		private static void SaveToCache(string url, byte[] data)
		{
			storage.Add(url, data);
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

		public static void ClearCacheForUrl(string url)
		{
			FileLog.Write("clear  : " + url);
			storage.Remove(url);
		}

		public static void ClearCache()
		{
			FileLog.Write("clear  : ALL");
			storage.RemoveAll();
		}
	}
}