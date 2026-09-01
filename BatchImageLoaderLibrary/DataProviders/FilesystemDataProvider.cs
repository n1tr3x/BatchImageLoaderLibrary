using BatchImageLoaderLibrary.DataProviders.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Trinet.Core.IO.Ntfs;

namespace BatchImageLoaderLibrary.DataProviders
{
	// Один файл на (url, variant): {sha1(url)}_{variant}.jpg. Исходный URL лежит
	// в NTFS-потоке «filename» (UTF-8) — по нему GetAll восстанавливает ключи.
	internal class FilesystemDataProvider : IDataProvider
	{
		private const string KeyStreamName = "filename";

		// Маска СВОИХ файлов: 40 hex-символов sha1, подчёркивание, вариант, .jpg.
		// По ней работает RemoveAll, чтобы не тронуть чужое содержимое каталога.
		private static readonly Regex OwnFileName = new("^[0-9a-f]{40}_[^\\\\/]+\\.jpg$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		public string CacheDirectory { get; }

		public FilesystemDataProvider(string cacheDirectory)
		{
			CacheDirectory = Path.GetFullPath(cacheDirectory);
		}

		public byte[]? Get(string url, string variant)
		{
			try
			{
				return File.ReadAllBytes(FilePath(url, variant));
			}
			catch (FileNotFoundException)
			{
				return null;
			}
			catch (DirectoryNotFoundException)
			{
				return null;
			}
		}

		public Dictionary<string, byte[]> GetAll(string variant)
		{
			Dictionary<string, byte[]> result = new Dictionary<string, byte[]>();
			if (!Directory.Exists(CacheDirectory))
				return result;

			// Только файлы нужного варианта — как и у SQLite-провайдера.
			foreach (string file in Directory.GetFiles(CacheDirectory, "*_" + variant + ".jpg"))
			{
				try
				{
					string key = ReadKey(file);
					// Сверяем ключ с именем файла: отсекает чужие/повреждённые ADS
					// и старые записи, где URL был сохранён в другой кодировке.
					if (!string.Equals(Path.GetFileName(file), FileName(key, variant), StringComparison.OrdinalIgnoreCase))
					{
						FileLog.Write("fs     : skip " + Path.GetFileName(file) + " (key/name mismatch)");
						continue;
					}
					result[key] = File.ReadAllBytes(file);
				}
				catch (Exception e)
				{
					Trace.WriteLine("FilesystemDataProvider.GetAll: failed to read " + file + ": " + e.Message);
					FileLog.Write("fs     : skip " + Path.GetFileName(file) + " (" + e.GetType().Name + ": " + e.Message + ")");
				}
			}

			return result;
		}

		public void Add(string url, string variant, byte[] data)
		{
			Directory.CreateDirectory(CacheDirectory);
			string path = FilePath(url, variant);
			File.WriteAllBytes(path, data);

			using FileStream keyStream = new FileInfo(path).GetAlternateDataStream(KeyStreamName, FileMode.Create).OpenWrite();
			byte[] keyBytes = Encoding.UTF8.GetBytes(url);
			keyStream.Write(keyBytes, 0, keyBytes.Length);
		}

		public void Remove(string url)
		{
			if (!Directory.Exists(CacheDirectory))
				return;

			// Удаляем ВСЕ варианты (размеры) этого URL: {hash}_*.jpg.
			foreach (string file in Directory.GetFiles(CacheDirectory, Hash(url) + "_*.jpg"))
				File.Delete(file);
		}

		public void RemoveAll()
		{
			if (!Directory.Exists(CacheDirectory))
				return;

			// Каталог не удаляем и чужие файлы не трогаем — только свои по маске.
			foreach (string file in Directory.GetFiles(CacheDirectory))
			{
				if (OwnFileName.IsMatch(Path.GetFileName(file)))
					File.Delete(file);
			}
		}

		private static string ReadKey(string file)
		{
			using StreamReader reader = new FileInfo(file).GetAlternateDataStream(KeyStreamName, FileMode.Open).OpenText();
			return reader.ReadToEnd();
		}

		private string FilePath(string url, string variant)
		{
			return Path.Combine(CacheDirectory, FileName(url, variant));
		}

		// Имя файла конкретного варианта: {hash}_{variant}.jpg
		private static string FileName(string url, string variant)
		{
			return Hash(url) + "_" + variant + ".jpg";
		}

		// SHA1 от URL — стабильная часть имени, общая для всех вариантов.
		private static string Hash(string url)
		{
			return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
		}
	}
}
