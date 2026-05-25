using System.Drawing;

namespace BatchImageLoaderLibrary
{
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
            try
            {
                return Image.FromStream(ms);
            }
            catch (Exception ex)
            {
                return null;
            }
        }
	}
}
