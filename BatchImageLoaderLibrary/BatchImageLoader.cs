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
		private static BatchImageLoader instance;
		private static ConcurrentQueue<string> UrlQueue = new();
		private static ConcurrentDictionary<string, CachedImage> Images = new();
		private static int threadsCount = 0;
		private static StorageFacade storage;
		private static int imagesLoading = 0;

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
				string resourceName = Array.Find(
					assembly.GetManifestResourceNames(),
					n => n.EndsWith("404.png", StringComparison.OrdinalIgnoreCase));

				if (resourceName == null)
					return Array.Empty<byte>();

				using Stream stream = assembly.GetManifestResourceStream(resourceName);
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

		public int ImagesProcessing => UrlQueue.Count + ImagesLoading;

		public int ImagesInQueue => UrlQueue.Count;

		public bool CreateThumbnails { get; set; } = true;

		public int ThumbnailWidth { get; set; } = 120;

		public int ThumbnailHeigth { get; set; } = 120;

		public bool NeedSaveToCache { get; set; } = true;

		public static StorageType StorageType = StorageType.DB;



#if DEBUG
		private static int LoadedPhotosCount = 1;
		private static long PhotosLoadingTime = 0;
		private static ConcurrentDictionary<string, int> Threads = new();
#endif


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

		public async Task<CachedImage> GetImageFromUrl(string url)
		{
#if DEBUG
			Trace.WriteLine("GetImageFromUrl " + url);
#endif
			if (!Images.TryAdd(url, new CachedImage()))
			{
				if (Images[url].Loaded())
				{
#if DEBUG
					Trace.WriteLine("Image " + url + " already loaded, NOT enqueued, ret image");
#endif
					return Images[url];
				}
				return await Task.Run(async () =>
				{
#if DEBUG
					Trace.WriteLine("Image " + url + " already enqueued, waiting for loading...");
#endif
					//int tryCount = 0;
					while (!Images[url].Loaded())
					{
#if DEBUG
						Trace.WriteLine("Image " + url + " not loaded yet, wait 500 ms and check again, images left = " + UrlQueue.Count);
						Trace.WriteLine("Images loaded = " + LoadedPhotosCount + ", avg time = " + PhotosLoadingTime / LoadedPhotosCount + " ms");
#endif
						await Task.Delay(500);
					}
#if DEBUG
					Trace.WriteLine("Image " + url + " loaded, ret image, images left = " + UrlQueue.Count);
#endif
					return Images[url];
				});
			}

			UrlQueue.Enqueue(url);
			Images[url] = new CachedImage();
#if DEBUG
			Trace.WriteLine("Image " + url + " enqueued");
#endif
			return await ProcessUrlAsync();
		}

        private async Task<CachedImage> ProcessUrlAsync()
		{
#if DEBUG
			Trace.WriteLine("ProcessUrlAsync begin, ThreadsCount = " + ThreadCount + ", images left = " + UrlQueue.Count);
#endif
			while (threadsCount > MaxThreadsCount)
				await Task.Delay(100);

			string url;
			if (UrlQueue.TryDequeue(out url))
			{
				Interlocked.Increment(ref threadsCount);
#if DEBUG
				Trace.WriteLine("Url " + url + " dequeued");
#endif
				byte[] data = LoadFromCache(url);
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
							data = CreateThumbnail(data);

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

				Images[url].Data = data;
				//Trace.WriteLine("Img with url " + url + " loaded, ret, image loading count = " + ImagesLoading);
				Interlocked.Decrement(ref threadsCount);
				return Images[url];
			}
			return null;
		}

		public static byte[] CreateThumbnail(byte[] image, int h = 120, int w = 120)
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
				return null;
			}
		}

		private static async Task<byte[]> LoadImage(string url)
		{
#if DEBUG
			Trace.WriteLine("LoadImage " + url);
#endif
			Interlocked.Increment(ref imagesLoading);
			byte[] bytes = null;
			try
			{
				bytes = await httpClient.GetByteArrayAsync(url);
			}
			catch (Exception e)
			{
			}

#if DEBUG
			Trace.WriteLine("LoadImage " + url + " OK");
#endif
			Interlocked.Decrement(ref imagesLoading);
			return bytes;
		}

		public async void LoadFromCache()
		{
			Dictionary<string, byte[]> data = await storage.GetAll();
			foreach ((string key, byte[] value) in data)
			{
				Images.TryAdd(key, new CachedImage(value));
			}
		}

		private static byte[] LoadFromCache(string url)
		{
#if DEBUG
			Trace.WriteLine("Try to LoadFromCache " + url);
#endif
			byte[] result = storage.Get(url);
#if DEBUG
			Trace.WriteLine("LoadFromCache " + url + (result != null ? " success" : " failed"));
#endif
			return result;
		}

		private static void SaveToCache(string url, byte[] data)
		{
			storage.Add(url, data);
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