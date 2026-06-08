using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
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

		// Один разделяемый HttpClient на всю библиотеку: переиспользует пул
		// соединений и не плодит сокеты в TIME_WAIT при пакетной загрузке.
		// PooledConnectionLifetime заставляет периодически пересоздавать
		// соединения, чтобы подхватывать изменения DNS у вечного клиента.
		private static readonly HttpClient httpClient = new HttpClient(
			new SocketsHttpHandler
			{
				PooledConnectionLifetime = TimeSpan.FromMinutes(5)
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
#if DEBUG
			Trace.WriteLine("GetImageFromUrl " + url);
#endif
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
			catch
			{
				// Не оставляем сбойную задачу в кэше — даём шанс повторить загрузку.
				Images.TryRemove(url, out _);
				throw;
			}
		}

        private async Task<CachedImage> ProcessUrlAsync(string url)
		{
			Interlocked.Increment(ref imagesInQueue);
#if DEBUG
			Trace.WriteLine("ProcessUrlAsync begin, ThreadsCount = " + ThreadCount + ", images waiting = " + imagesInQueue);
#endif
			// Жёсткий лимит параллелизма: семафор не протекает (в отличие от
			// прежней проверки threadsCount ДО инкремента) и не крутит busy-wait.
			SemaphoreSlim throttle = GetThrottle();
			await throttle.WaitAsync();
			Interlocked.Increment(ref threadsCount);
			Interlocked.Decrement(ref imagesInQueue);
			try
			{
#if DEBUG
				Trace.WriteLine("Url " + url + " processing");
#endif
				// Размер превью (или orig) входит в ключ кэша, поэтому и чтение,
				// и запись должны идти под одним вариантом.
				storage.Variant = CurrentVariant();
				byte[]? data = LoadFromCache(url);
				if (data == null)
				{
#if DEBUG
					Trace.WriteLine("Trying to load " + url);
#endif
					data = await LoadImage(url);
#if DEBUG
					if (data == null)
						Trace.WriteLine("Url " + url + " NOT loaded, data is NULL");
					else
						Trace.WriteLine("Url " + url + " loaded, data len = " + data.Length);
#endif
					bool loadFailed = false;
					if (data == null || data.Length == 0)
					{
						loadFailed = true;
						data = NotFoundImage;
					}
					else
					{
						if (CreateThumbnails)
							data = CreateThumbnail(data, ThumbnailHeigth, ThumbnailWidth);

						if (data == null)
						{
							loadFailed = true;
							data = NotFoundImage;
						}
					}

					// Не кэшируем заглушку 404: иначе временный сбой сети навсегда
					// «отравит» URL — на следующих запусках вернётся 404 без ретрая.
					if (NeedSaveToCache && !loadFailed)
						SaveToCache(url, data);
				}

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
				Trace.WriteLine("CreateThumbnail failed: " + e.Message);
				return null;
			}
		}

		private static async Task<byte[]?> LoadImage(string url)
		{
#if DEBUG
			Trace.WriteLine("LoadImage " + url);
#endif
			Interlocked.Increment(ref imagesLoading);
			byte[]? bytes = null;
			try
			{
				bytes = await httpClient.GetByteArrayAsync(url);
			}
			catch (Exception e)
			{
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
			Dictionary<string, byte[]> data = await storage.GetAll();
			foreach ((string key, byte[] value) in data)
			{
				Images.TryAdd(key, new Lazy<Task<CachedImage>>(Task.FromResult(new CachedImage(value))));
			}
		}

		private static byte[]? LoadFromCache(string url)
		{
#if DEBUG
			Trace.WriteLine("Try to LoadFromCache " + url);
#endif
			byte[]? result = storage.Get(url);
#if DEBUG
			Trace.WriteLine("LoadFromCache " + url + (result != null ? " success" : " failed"));
#endif
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
			storage.Remove(url);
		}

		public static void ClearCache()
		{
			storage.RemoveAll();
		}
	}
}