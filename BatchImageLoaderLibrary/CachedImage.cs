using System.Diagnostics;
using System.Drawing;

namespace BatchImageLoaderLibrary
{
	public class CachedImage
	{
		private volatile byte[]? data;

		public CachedImage()
		{
		}

		public CachedImage(byte[] data)
		{
			Data = data;
		}

		public byte[]? Data
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
			return data?.Length ?? 0;
		}

		public byte[]? ToByteArray()
		{
			return Data;
		}

		public Image? ToImage()
		{
			if (data == null)
				return null;

			// MemoryStream должен жить, пока живёт Image (GDI+ читает из него
			// лениво), поэтому в успешной ветке его НЕ закрываем; закрываем
			// только при ошибке, где Image создан не будет.
			MemoryStream ms = new MemoryStream(data);
			try
			{
				return Image.FromStream(ms);
			}
			catch (Exception ex)
			{
				Trace.WriteLine("ToImage failed: " + ex.Message);
				ms.Dispose();
				return null;
			}
		}
	}
}
