using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using BatchImageLoaderLibrary;
using Microsoft.Data.Sqlite;
using Xunit;

// Загрузчик — статический синглтон, поэтому тесты выполняются строго последовательно.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BatchImageLoaderLibraryTests
{
	// Подменный транспорт: отвечает делегатом, считает вызовы и одновременные запросы.
	public sealed class FakeHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
		private int calls;
		private int inFlight;
		private int maxInFlight;

		public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
		{
			this.respond = respond;
		}

		public static FakeHandler Returning(byte[] body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "image/png")
		{
			return new FakeHandler((_, _) => Task.FromResult(Response(body, status, contentType)));
		}

		public static HttpResponseMessage Response(byte[] body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "image/png")
		{
			HttpResponseMessage response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
			response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
			return response;
		}

		public int Calls => calls;

		public int InFlight => inFlight;

		public int MaxInFlight => maxInFlight;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref calls);
			int now = Interlocked.Increment(ref inFlight);
			int seen;
			do
			{
				seen = maxInFlight;
			}
			while (now > seen && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen);

			try
			{
				return await respond(request, cancellationToken);
			}
			finally
			{
				Interlocked.Decrement(ref inFlight);
			}
		}
	}

	internal static class TestImage
	{
		public static byte[] Png(int width = 8, int height = 8)
		{
			using Bitmap bitmap = new Bitmap(width, height);
			using (Graphics g = Graphics.FromImage(bitmap))
				g.Clear(Color.Red);
			using MemoryStream ms = new MemoryStream();
			bitmap.Save(ms, ImageFormat.Png);
			return ms.ToArray();
		}

		public static Size Decode(byte[] data)
		{
			using MemoryStream ms = new MemoryStream(data);
			using Image image = Image.FromStream(ms);
			return image.Size;
		}
	}

	// Каждый тест — чистый синглтон и свой временный каталог под кэш.
	public abstract class LoaderTestBase : IDisposable
	{
		protected string TempDir { get; }

		protected LoaderTestBase()
		{
			TempDir = Path.Combine(Path.GetTempPath(), "BatchImageLoaderLibraryTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(TempDir);
			BatchImageLoader.ResetForTests();
			BatchImageLoader.CacheDirectory = Path.Combine(TempDir, "cache");
			BatchImageLoader.DatabasePath = Path.Combine(TempDir, "cache.sqlite");
		}

		// Имитация перезапуска процесса: память и транспорт сброшены, кэш на диске тот же.
		protected static void Restart(StorageType storage, FakeHandler handler)
		{
			string cacheDirectory = BatchImageLoader.CacheDirectory;
			string databasePath = BatchImageLoader.DatabasePath;
			BatchImageLoader.ResetForTests();
			BatchImageLoader.CacheDirectory = cacheDirectory;
			BatchImageLoader.DatabasePath = databasePath;
			BatchImageLoader.StorageType = storage;
			BatchImageLoader.HttpHandler = handler;
		}

		protected static BatchImageLoader Loader(StorageType storage, FakeHandler handler, bool thumbnails = false)
		{
			BatchImageLoader.StorageType = storage;
			BatchImageLoader.HttpHandler = handler;
			BatchImageLoader loader = BatchImageLoader.Instance;
			loader.CreateThumbnails = thumbnails;
			return loader;
		}

		// Число записей в персистентном кэше (всех вариантов).
		protected static int CachedEntries()
		{
			if (BatchImageLoader.StorageType == StorageType.FILE)
			{
				string directory = BatchImageLoader.CacheDirectory;
				return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.jpg").Length : 0;
			}

			if (!File.Exists(BatchImageLoader.DatabasePath))
				return 0;
			using SqliteConnection connection = new SqliteConnection("Data Source=" + BatchImageLoader.DatabasePath);
			connection.Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT COUNT(*) FROM images";
			return Convert.ToInt32(command.ExecuteScalar());
		}

		public void Dispose()
		{
			BatchImageLoader.ResetForTests();
			try
			{
				Directory.Delete(TempDir, true);
			}
			catch (IOException)
			{
				// временный каталог, не критично
			}
		}
	}
}
