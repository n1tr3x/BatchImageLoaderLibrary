using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;

namespace BatchImageLoaderLibrary
{
    public class BatchImageLoader
    {
        private static readonly object LockObject = new();

        private static ConcurrentQueue<string> UrlQueue = new();
        private static ConcurrentDictionary<string, CachedImage> Images = new();
        private static int ThreadsCount = 0;
        private static int ImagesLoading = 0;

        private static BatchImageLoader instance;

        private BatchImageLoader()
        {
        }

        public static BatchImageLoader Instance
        {
            get
            {
                lock (LockObject)
                {
                    if (instance == null)
                        instance = new BatchImageLoader();
                }

                return instance;
            }
        }

        public int ImagesProcessing()
        {
            return UrlQueue.Count + ImagesLoading;
        }

        public async Task<CachedImage> GetImageFromUrl(string url)
        {
            if (Images.ContainsKey(url))
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
                    while (!Images[url].Loaded())
                    {
#if DEBUG
                        Trace.WriteLine("Image not loaded yet, wait 200 ms and check again");
#endif
                        await Task.Delay(200);
                    }
#if DEBUG
                    Trace.WriteLine("Image " + url + " loaded, ret image");
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
            Trace.WriteLine("ProcessUrlAsync begin, ThreadsCount = " + ThreadsCount + ", images left = " + UrlQueue.Count);
#endif
            while (ThreadsCount > 64)
                await Task.Delay(1000);

            Interlocked.Increment(ref ThreadsCount);

            //Trace.WriteLine("ProcessUrlAsync started");

            string url;
            if (UrlQueue.TryDequeue(out url))
            {
                //Trace.WriteLine("Try to process url " + url);

                byte[] data = LoadFromCache(url);
                if (data == null)
                {
                    data = await LoadImage(url);
                    if (data == null || data.Length == 0)
                    {
                        data = File.ReadAllBytes(@"404.png");
                    }
                    else
                    {
                        data = CreateThumbnail(data);
                    }

                    SaveToCache(url, data);
                }

                Images[url].Data = data;
                //Trace.WriteLine("Img with url " + url + " loaded, ret, image loading count = " + ImagesLoading);
                Interlocked.Decrement(ref ThreadsCount);
                return Images[url];
            }

            return null;
        }

        public static byte[] CreateThumbnail(byte[] PassedImage)
        {
            using (MemoryStream ms = new MemoryStream(PassedImage, 0, PassedImage.Length))
            {
                using (Image img = Image.FromStream(ms))
                {
                    int h = 120;
                    int w = 120;

                    using (Bitmap b = new Bitmap(img, new Size(w, h)))
                    {
                        using (MemoryStream ms2 = new MemoryStream())
                        {
                            b.Save(ms2, System.Drawing.Imaging.ImageFormat.Jpeg);
                            return ms2.ToArray();
                        }
                    }
                }
            }

            return null;
        }

        private async Task<byte[]> LoadImage(string url)
        {
            Interlocked.Increment(ref ImagesLoading);
            //Trace.WriteLine("LoadImage START");
            byte[] bytes = null;
            //Task.Delay(2000).Wait();
            var httpClient = new HttpClient();
            //httpClient.Timeout = TimeSpan.FromSeconds(10);
            try
            {
                bytes = await httpClient.GetByteArrayAsync(url);
            }
            catch (Exception e)
            {
                //Trace.WriteLine("LoadImage EXEPTION");
                //Trace.WriteLine(e);
            }

            //Trace.WriteLine("LoadImage END");
            Interlocked.Decrement(ref ImagesLoading);
            return bytes;
        }

        private static byte[] LoadFromCache(string url)
        {
            string imageFileName = GetImagePath(url);
            if (File.Exists(imageFileName))
            {
                return File.ReadAllBytes(imageFileName);
            }

            return null;
        }

        private static bool SaveToCache(string url, byte[] data)
        {
            if (!Directory.Exists("cache"))
                Directory.CreateDirectory("cache");
            string imageFileName = GetImagePath(url);
            if (!File.Exists(imageFileName))
            {
                File.WriteAllBytesAsync(imageFileName, data);
                return true;
            }

            return false;
        }

        private static string GetImagePath(string url)
        {
            return "cache/" + url.Replace(":", "").Replace("/", "").Replace("?", "").Replace("*", "");
        }
    }

    public class CachedImage
    {
        private volatile byte[] data;

        public CachedImage()
        {
        }

        public CachedImage(byte[] data)
        {
            Data = data;
        }

        public byte[] Data
        {
            get => data;
            set => data = value;
        }

        public bool Loaded()
        {
            return data != null && data.Length > 0;
        }

        public int Size()
        {
            return data.Length;
        }

        public byte[] ToByteArray()
        {
            return Data;
        }
    }
}