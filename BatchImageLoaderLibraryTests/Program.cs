using BatchImageLoaderLibrary;

List<string> imgs = new List<string>()
{
	"https://sun9-24.userapi.com/impg/LGNP5syRWLZDe6KgeUPeaoUJoQbE0fC347gRtw/FuUX96lEgdw.jpg?size=960x384&quality=95&crop=0,522,2560,1023&sign=8e8e3cf3d5a541a2dc7fbf075b92aebb&c_uniq_tag=ZqWGkxnilcI2QYq05aJVu_kCRCjU17Tx3DzUEjAQpMU&type=helpers",
	"https://sun9-10.userapi.com/impf/c857632/v857632383/2191c/bL1hpLBUm44.jpg?size=2560x1707&quality=96&sign=ab18cabc112c17450a46e3461d0bdcad&type=album"
};

BatchImageLoader.StorageType = StorageType.FILE;
BatchImageLoader.Instance.LoadFromCache();

foreach (string img in imgs)
{
	BatchImageLoader.Instance.GetImageFromUrl(img);
}
BatchImageLoader.Instance.LoadFromCache();
//BatchImageLoader.Instance.GetImageFromUrl(@"https://sun9-37.userapi.com/impg/ycC_AEalvXdJnuMfaqN_Owv6ySyNaqan9SXF7g/QNppWrs6xfs.jpg?size=1215x2160&quality=95&sign=453e659c0c48ec55577a23aaeeab2d35&type=album");


await Task.Delay(11233);

Console.ReadKey();