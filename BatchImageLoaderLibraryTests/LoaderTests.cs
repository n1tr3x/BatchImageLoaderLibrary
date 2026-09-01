using System.Net;
using System.Text;
using BatchImageLoaderLibrary;
using Xunit;

namespace BatchImageLoaderLibraryTests
{
	public class LoaderTests : LoaderTestBase
	{
		[Theory]
		[InlineData(StorageType.FILE)]
		[InlineData(StorageType.DB)]
		public async Task SameUrl_LoadsOnce_AllCallersShareResult(StorageType storage)
		{
			TaskCompletionSource gate = new TaskCompletionSource();
			byte[] png = TestImage.Png();
			FakeHandler handler = new FakeHandler(async (_, _) =>
			{
				await gate.Task;
				return FakeHandler.Response(png);
			});
			BatchImageLoader loader = Loader(storage, handler);

			Task<CachedImage>[] tasks = Enumerable.Range(0, 10)
				.Select(_ => loader.GetImageFromUrl("http://x/a.png"))
				.ToArray();
			gate.SetResult();
			CachedImage[] results = await Task.WhenAll(tasks);

			Assert.Equal(1, handler.Calls);
			Assert.All(results, r => Assert.Same(results[0], r));
			Assert.False(results[0].IsPlaceholder);
			Assert.Equal(png, results[0].Data);
			Assert.Equal(1, CachedEntries());
		}

		[Fact]
		public async Task Throttle_NeverExceedsMaxThreadsCount()
		{
			TaskCompletionSource gate = new TaskCompletionSource();
			FakeHandler handler = new FakeHandler(async (_, _) =>
			{
				await gate.Task;
				return FakeHandler.Response(TestImage.Png());
			});
			BatchImageLoader loader = Loader(StorageType.FILE, handler);
			loader.MaxThreadsCount = 2;

			Task<CachedImage>[] tasks = Enumerable.Range(0, 6)
				.Select(i => loader.GetImageFromUrl("http://x/" + i + ".png"))
				.ToArray();
			Assert.True(SpinWait.SpinUntil(() => handler.InFlight == 2, TimeSpan.FromSeconds(5)));
			await Task.Delay(200);
			Assert.Equal(2, handler.InFlight);
			Assert.Equal(4, loader.ImagesInQueue);

			gate.SetResult();
			await Task.WhenAll(tasks);

			Assert.Equal(6, handler.Calls);
			Assert.Equal(2, handler.MaxInFlight);
			Assert.Equal(0, loader.ImagesProcessing);
			Assert.Equal(6, CachedEntries());
		}

		[Theory]
		[InlineData(StorageType.FILE)]
		[InlineData(StorageType.DB)]
		public async Task Failure_ReturnsPlaceholder_NotPersisted_RetriesOnNextCall(StorageType storage)
		{
			FakeHandler handler = FakeHandler.Returning(Array.Empty<byte>(), HttpStatusCode.InternalServerError);
			BatchImageLoader loader = Loader(storage, handler);

			CachedImage first = await loader.GetImageFromUrl("http://x/broken.jpg");

			Assert.True(first.IsPlaceholder);
			Assert.Equal(BatchImageLoader.PlaceholderBytes, first.Data);
			Assert.True(first.Loaded());
			Assert.Equal(0, CachedEntries());
			Assert.Equal(0, loader.ImagesLoaded);

			CachedImage second = await loader.GetImageFromUrl("http://x/broken.jpg");

			Assert.Equal(2, handler.Calls);
			Assert.True(second.IsPlaceholder);
		}

		[Fact]
		public async Task NonImage200_IsPlaceholder_NotCached_WhenThumbnailsOff()
		{
			byte[] html = Encoding.UTF8.GetBytes("<!doctype html><html><body>Please solve the captcha</body></html>");
			FakeHandler handler = FakeHandler.Returning(html, contentType: "text/html");
			BatchImageLoader loader = Loader(StorageType.FILE, handler, thumbnails: false);

			CachedImage image = await loader.GetImageFromUrl("http://x/photo.jpg");

			Assert.True(image.IsPlaceholder);
			Assert.Equal(0, CachedEntries());
		}

		[Fact]
		public async Task OversizedResponse_IsPlaceholder_NotCached()
		{
			byte[] body = TestImage.Png().Concat(new byte[10_000]).ToArray();
			FakeHandler handler = FakeHandler.Returning(body);
			BatchImageLoader loader = Loader(StorageType.FILE, handler);
			BatchImageLoader.MaxImageBytes = 1024;

			CachedImage image = await loader.GetImageFromUrl("http://x/huge.png");

			Assert.True(image.IsPlaceholder);
			Assert.Equal(0, CachedEntries());
		}

		[Theory]
		[InlineData(StorageType.FILE)]
		[InlineData(StorageType.DB)]
		public async Task ClearCacheForUrl_EvictsMemoryAndDisk(StorageType storage)
		{
			FakeHandler handler = FakeHandler.Returning(TestImage.Png());
			BatchImageLoader loader = Loader(storage, handler);
			const string url = "http://x/1.png";

			await loader.GetImageFromUrl(url);
			await loader.GetImageFromUrl(url);
			Assert.Equal(1, handler.Calls);
			Assert.Equal(1, CachedEntries());

			BatchImageLoader.ClearCacheForUrl(url);
			Assert.Equal(0, CachedEntries());
			Assert.Equal(0, loader.ImagesLoaded);

			await loader.GetImageFromUrl(url);
			Assert.Equal(2, handler.Calls);
			Assert.Equal(1, CachedEntries());
		}

		[Theory]
		[InlineData(StorageType.FILE)]
		[InlineData(StorageType.DB)]
		public async Task PersistentCache_ServesWithoutHttp_AfterRestart(StorageType storage)
		{
			byte[] png = TestImage.Png();
			BatchImageLoader loader = Loader(storage, FakeHandler.Returning(png));
			await loader.GetImageFromUrl("http://x/p.png");

			FakeHandler failing = FakeHandler.Returning(Array.Empty<byte>(), HttpStatusCode.NotFound);
			Restart(storage, failing);
			BatchImageLoader.Instance.CreateThumbnails = false;

			CachedImage image = await BatchImageLoader.Instance.GetImageFromUrl("http://x/p.png");

			Assert.False(image.IsPlaceholder);
			Assert.Equal(png, image.Data);
			Assert.Equal(0, failing.Calls);
		}

		[Theory]
		[InlineData(StorageType.FILE)]
		[InlineData(StorageType.DB)]
		public async Task LoadFromCache_PreloadsCurrentVariant_NonAsciiUrlRoundTrips(StorageType storage)
		{
			byte[] png = TestImage.Png();
			const string url = "https://пример.рф/фото/кот.png?размер=big";
			BatchImageLoader loader = Loader(storage, FakeHandler.Returning(png));
			await loader.GetImageFromUrl(url);

			FakeHandler failing = FakeHandler.Returning(Array.Empty<byte>(), HttpStatusCode.NotFound);
			Restart(storage, failing);
			BatchImageLoader.Instance.CreateThumbnails = false;

			await BatchImageLoader.Instance.LoadFromCache();
			Assert.Equal(1, BatchImageLoader.Instance.ImagesLoaded);

			CachedImage image = await BatchImageLoader.Instance.GetImageFromUrl(url);
			Assert.Equal(png, image.Data);
			Assert.Equal(0, failing.Calls);
		}

		[Theory]
		[InlineData(StorageType.FILE)]
		[InlineData(StorageType.DB)]
		public async Task ThumbnailVariants_Coexist_AndClearRemovesAll(StorageType storage)
		{
			const string url = "http://x/big.png";
			BatchImageLoader loader = Loader(storage, FakeHandler.Returning(TestImage.Png(64, 64)), thumbnails: true);
			loader.ThumbnailWidth = 16;
			loader.ThumbnailHeigth = 16;

			CachedImage small = await loader.GetImageFromUrl(url);
			Assert.False(small.IsPlaceholder);
			Assert.Equal(new System.Drawing.Size(16, 16), TestImage.Decode(small.Data!));
			Assert.Equal(1, CachedEntries());

			// Другой размер — как другое приложение с тем же кэшем.
			FakeHandler second = FakeHandler.Returning(TestImage.Png(64, 64));
			Restart(storage, second);
			BatchImageLoader.Instance.CreateThumbnails = true;
			BatchImageLoader.Instance.ThumbnailWidth = 32;
			BatchImageLoader.Instance.ThumbnailHeigth = 32;

			CachedImage medium = await BatchImageLoader.Instance.GetImageFromUrl(url);
			Assert.Equal(new System.Drawing.Size(32, 32), TestImage.Decode(medium.Data!));
			Assert.Equal(1, second.Calls);
			Assert.Equal(2, CachedEntries());

			// Предзагрузка берёт только текущий вариант.
			Restart(storage, FakeHandler.Returning(Array.Empty<byte>(), HttpStatusCode.NotFound));
			BatchImageLoader.Instance.CreateThumbnails = true;
			BatchImageLoader.Instance.ThumbnailWidth = 32;
			BatchImageLoader.Instance.ThumbnailHeigth = 32;
			await BatchImageLoader.Instance.LoadFromCache();
			Assert.Equal(1, BatchImageLoader.Instance.ImagesLoaded);
			Assert.Equal(new System.Drawing.Size(32, 32), TestImage.Decode((await BatchImageLoader.Instance.GetImageFromUrl(url)).Data!));

			BatchImageLoader.ClearCacheForUrl(url);
			Assert.Equal(0, CachedEntries());
		}

		[Fact]
		public async Task ClearCache_File_DeletesOnlyOwnFiles_KeepsDirectory()
		{
			BatchImageLoader loader = Loader(StorageType.FILE, FakeHandler.Returning(TestImage.Png()));
			await loader.GetImageFromUrl("http://x/1.png");
			string foreign = Path.Combine(BatchImageLoader.CacheDirectory, "keep-me.txt");
			File.WriteAllText(foreign, "not ours");

			BatchImageLoader.ClearCache();

			Assert.True(File.Exists(foreign));
			Assert.True(Directory.Exists(BatchImageLoader.CacheDirectory));
			Assert.Equal(0, CachedEntries());
			Assert.Equal(0, loader.ImagesLoaded);
		}

		[Fact]
		public void ClearCache_BeforeInstance_DoesNotThrow()
		{
			BatchImageLoader.StorageType = StorageType.FILE;
			BatchImageLoader.ClearCache();
			BatchImageLoader.ClearCacheForUrl("http://x/none.png");
		}

		[Fact]
		public void CreateThumbnail_DecodesEmbeddedPlaceholder()
		{
			byte[]? thumbnail = BatchImageLoader.CreateThumbnail(BatchImageLoader.PlaceholderBytes, 24, 48);

			Assert.NotNull(thumbnail);
			Assert.Equal(new System.Drawing.Size(48, 24), TestImage.Decode(thumbnail));
		}

		[Fact]
		public async Task StorageException_IsThrownToCaller_AndUrlIsRetried()
		{
			FakeHandler handler = FakeHandler.Returning(TestImage.Png());
			BatchImageLoader loader = Loader(StorageType.FILE, handler);
			// Каталог кэша занят файлом с тем же именем — запись в кэш упадёт.
			File.WriteAllText(BatchImageLoader.CacheDirectory, "blocker");

			await Assert.ThrowsAnyAsync<IOException>(() => loader.GetImageFromUrl("http://x/1.png"));
			Assert.Equal(0, loader.ImagesLoaded);

			File.Delete(BatchImageLoader.CacheDirectory);
			CachedImage image = await loader.GetImageFromUrl("http://x/1.png");
			Assert.False(image.IsPlaceholder);
			Assert.Equal(2, handler.Calls);
		}
	}
}
