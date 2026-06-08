using BatchImageLoaderLibrary.DataProviders.Interfaces;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Trinet.Core.IO.Ntfs;

namespace BatchImageLoaderLibrary.DataProviders
{
	internal class FilesystemDataProvider : IDataProvider
	{
		public string CacheDirectory { get; set; }

		// Вариант кэшируемой картинки, попадает в конец имени файла:
		// "120x120" для превью или "orig" для полноразмера. Задаётся
		// загрузчиком из CreateThumbnails + ThumbnailWidth/Height.
		public string Variant { get; set; } = "orig";


		public FilesystemDataProvider(string cacheDirectory)
		{
			CacheDirectory = cacheDirectory;
		}

		public byte[]? Get(string filename)
		{
			if (File.Exists(Path.Combine(CacheDirectory, NormalizeUrl(filename))))
			{
#if DEBUG
				Trace.WriteLine("LoadFromCache " + filename + " success");
#endif
				return File.ReadAllBytes(Path.Combine(CacheDirectory, NormalizeUrl(filename)));
			}

			return null;
		}

		public async Task<Dictionary<string, byte[]>> GetAll()
		{
			if (!Directory.Exists(CacheDirectory))
				Directory.CreateDirectory(CacheDirectory);

			string[] fileNames = Directory.GetFiles(CacheDirectory);

			ConcurrentDictionary<string, byte[]> result = new ConcurrentDictionary<string, byte[]>();

			Task[] tasks = fileNames.Select(async fn =>
			{
                try
                {
                    StreamReader ntfsReader = (new FileInfo(fn)).GetAlternateDataStream("filename", FileMode.Open).OpenText();
                    result.TryAdd(ntfsReader.ReadToEnd(), File.ReadAllBytes(fn));
                    ntfsReader.Close();
                }
                catch (Exception e)
                {
                    Trace.WriteLine("FilesystemDataProvider.GetAll: failed to read " + fn + ": " + e.Message);
                }
			}).ToArray();

			await Task.WhenAll(tasks);

			return result.ToDictionary(e => e.Key, e => e.Value);
		}

		public void Add(string filename, byte[] data)
		{
			if (!Directory.Exists(CacheDirectory))
				Directory.CreateDirectory(CacheDirectory);

			File.WriteAllBytes(Path.Combine(CacheDirectory, NormalizeUrl(filename)), data);
			FileStream ntfsWriter = (new FileInfo(Path.Combine(CacheDirectory, NormalizeUrl(filename)))).GetAlternateDataStream("filename", FileMode.Create).OpenWrite();
			ntfsWriter.Write(Encoding.ASCII.GetBytes(filename));
			ntfsWriter.Close();
		}

		public void Update(string filename, byte[] data)
		{
			Add(filename, data);
		}

		public void Remove(string filename)
		{
			if (!Directory.Exists(CacheDirectory))
				return;

			// Удаляем ВСЕ варианты (размеры) этого URL: {hash}_*.jpg.
			string pattern = Hash(filename) + "_*.jpg";
			foreach (string file in Directory.GetFiles(CacheDirectory, pattern))
				File.Delete(file);
		}

		public void RemoveAll()
		{
			if (Directory.Exists(CacheDirectory))
				Directory.Delete(CacheDirectory, true);
		}

		// SHA1 от URL — стабильная часть имени, общая для всех вариантов.
		private string Hash(string url)
		{
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(url));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

		// Имя файла конкретного варианта: {hash}_{variant}.jpg
		private string NormalizeUrl(string url)
		{
            return Hash(url) + "_" + Variant + ".jpg";
        }
	}
}
