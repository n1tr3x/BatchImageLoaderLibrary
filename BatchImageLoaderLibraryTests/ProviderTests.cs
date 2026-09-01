using BatchImageLoaderLibrary.DataProviders;
using BatchImageLoaderLibrary.DataProviders.Interfaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BatchImageLoaderLibraryTests
{
	// Оба бэкенда обязаны вести себя одинаково — это контракт IDataProvider.
	public class ProviderTests : IDisposable
	{
		private readonly string tempDir = Path.Combine(Path.GetTempPath(), "BatchImageLoaderLibraryTests", Guid.NewGuid().ToString("N"));

		private IDataProvider Create(bool filesystem)
		{
			Directory.CreateDirectory(tempDir);
			return filesystem
				? new FilesystemDataProvider(Path.Combine(tempDir, "cache"))
				: new SQLiteDataProvider(Path.Combine(tempDir, "cache.sqlite"));
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void Providers_BehaveIdentically(bool filesystem)
		{
			IDataProvider provider = Create(filesystem);

			provider.Add("u1", "a", new byte[] { 1 });
			provider.Add("u1", "b", new byte[] { 2 });
			provider.Add("u2", "a", new byte[] { 3 });

			Assert.Equal(new byte[] { 1 }, provider.Get("u1", "a"));
			Assert.Equal(new byte[] { 2 }, provider.Get("u1", "b"));
			Assert.Null(provider.Get("u1", "c"));
			Assert.Null(provider.Get("missing", "a"));

			Dictionary<string, byte[]> all = provider.GetAll("a");
			Assert.Equal(2, all.Count);
			Assert.Equal(new byte[] { 1 }, all["u1"]);
			Assert.Equal(new byte[] { 3 }, all["u2"]);

			provider.Add("u1", "a", new byte[] { 9 });
			Assert.Equal(new byte[] { 9 }, provider.Get("u1", "a"));

			provider.Remove("u1");
			Assert.Null(provider.Get("u1", "a"));
			Assert.Null(provider.Get("u1", "b"));
			Assert.NotNull(provider.Get("u2", "a"));

			provider.RemoveAll();
			Assert.Empty(provider.GetAll("a"));
			Assert.Null(provider.Get("u2", "a"));
		}

		[Fact]
		public void Filesystem_GetAll_SkipsFilesWhoseKeyDoesNotMatchName()
		{
			IDataProvider provider = Create(filesystem: true);
			provider.Add("http://x/real.jpg", "orig", new byte[] { 1 });
			string directory = Path.Combine(tempDir, "cache");
			// Чужой файл с «нашим» расширением и без ADS-ключа.
			File.WriteAllBytes(Path.Combine(directory, "0000000000000000000000000000000000000000_orig.jpg"), new byte[] { 2 });
			File.WriteAllText(Path.Combine(directory, "notes.txt"), "keep");

			Dictionary<string, byte[]> all = provider.GetAll("orig");

			Assert.Single(all);
			Assert.Equal(new byte[] { 1 }, all["http://x/real.jpg"]);

			provider.RemoveAll();
			Assert.True(File.Exists(Path.Combine(directory, "notes.txt")));
		}

		public void Dispose()
		{
			SqliteConnection.ClearAllPools();
			try
			{
				Directory.Delete(tempDir, true);
			}
			catch (IOException)
			{
				// временный каталог, не критично
			}
		}
	}
}
