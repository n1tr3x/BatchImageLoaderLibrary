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
#if DEBUG
            Trace.WriteLine("GetImageFromUrl " + url);
#endif
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
                        Trace.WriteLine("Image " + url + " not loaded yet, wait 200 ms and check again");
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
                    if (data == null || data.Length == 0)
                    {
                        data = File.ReadAllBytes(@"404.png");
                    }
                    else
                    {
                        data = CreateThumbnail(data);
                        if (data == null)
                            data  = File.ReadAllBytes(@"404.png");
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
            try
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
            }
            catch (Exception e)
            {
                return null;
            }
        }

        private async Task<byte[]> LoadImage(string url)
        {
#if DEBUG
            Trace.WriteLine("LoadImage " + url);
#endif
            Interlocked.Increment(ref ImagesLoading);
            byte[] bytes = null;
            var httpClient = new HttpClient();
            try
            {
                bytes = await httpClient.GetByteArrayAsync(url);
            }
            catch (Exception e)
            {
#if DEBUG
                Trace.WriteLine("LoadImage Exception " + e);
#endif
            }

#if DEBUG
            Trace.WriteLine("LoadImage " + url + " OK");
#endif
            Interlocked.Decrement(ref ImagesLoading);
            return bytes;
        }

        private static byte[] LoadFromCache(string url)
        {
#if DEBUG
            Trace.WriteLine("Try to LoadFromCache " + url);
#endif
            string imageFileName = GetImagePath(url);
            if (File.Exists(imageFileName))
            {
#if DEBUG
                Trace.WriteLine("LoadFromCache " + url + " success");
#endif
                return File.ReadAllBytes(imageFileName);
            }
#if DEBUG
            Trace.WriteLine("LoadFromCache " + url + " failed");
#endif
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
            return data?.Length > 0;
        }

        public int Size()
        {
            return data.Length;
        }

        public byte[] ToByteArray()
        {
            return Data;
        }

        public Image ToImage()
        {
            MemoryStream ms = new MemoryStream(data);
            return Image.FromStream(ms);
        }
    }
}