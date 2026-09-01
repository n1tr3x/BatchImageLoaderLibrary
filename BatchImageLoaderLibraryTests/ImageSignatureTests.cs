using System.Text;
using BatchImageLoaderLibrary;
using Xunit;

namespace BatchImageLoaderLibraryTests
{
	public class ImageSignatureTests
	{
		public static IEnumerable<object[]> Images()
		{
			yield return new object[] { "jpeg", Pad(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }) };
			yield return new object[] { "png", TestImage.Png() };
			yield return new object[] { "gif", Pad(Encoding.ASCII.GetBytes("GIF89a")) };
			yield return new object[] { "bmp", Pad(Encoding.ASCII.GetBytes("BM")) };
			yield return new object[] { "webp", Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ") };
			yield return new object[] { "tiff", Pad(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) };
			yield return new object[] { "heic", Pad(new byte[] { 0, 0, 0, 0x18 }.Concat(Encoding.ASCII.GetBytes("ftypheic")).ToArray()) };
		}

		[Theory]
		[MemberData(nameof(Images))]
		public void RecognizesImageFormats(string name, byte[] data)
		{
			Assert.True(ImageSignature.IsImage(data), name);
		}

		[Theory]
		[InlineData("<!doctype html><html></html>")]
		[InlineData("{\"error\":\"captcha\"}")]
		[InlineData("")]
		[InlineData("short")]
		public void RejectsNonImages(string text)
		{
			Assert.False(ImageSignature.IsImage(Encoding.UTF8.GetBytes(text)));
		}

		private static byte[] Pad(byte[] head)
		{
			return head.Concat(new byte[16]).ToArray();
		}
	}
}
