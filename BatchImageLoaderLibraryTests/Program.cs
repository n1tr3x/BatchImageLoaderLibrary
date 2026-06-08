using BatchImageLoaderLibrary;

List<string> imgs = new List<string>()
{
	"https://sun9-24.userapi.com/impg/LGNP5syRWLZDe6KgeUPeaoUJoQbE0fC347gRtw/FuUX96lEgdw.jpg?size=960x384&quality=95&crop=0,522,2560,1023&sign=8e8e3cf3d5a541a2dc7fbf075b92aebb&c_uniq_tag=ZqWGkxnilcI2QYq05aJVu_kCRCjU17Tx3DzUEjAQpMU&type=helpers",
	"https://sun9-10.userapi.com/impf/c857632/v857632383/2191c/bL1hpLBUm44.jpg?size=2560x1707&quality=96&sign=ab18cabc112c17450a46e3461d0bdcad&type=album"
};

BatchImageLoader.StorageType = StorageType.FILE;
await BatchImageLoader.Instance.LoadFromCache();

List<Task<CachedImage>> tasks = new();
foreach (string img in imgs)
{
	tasks.Add(BatchImageLoader.Instance.GetImageFromUrl(img));
}
await Task.WhenAll(tasks);

await BatchImageLoader.Instance.LoadFromCache();

Console.WriteLine("Loaded " + BatchImageLoader.Instance.ImagesLoaded + " images");
Console.ReadKey();