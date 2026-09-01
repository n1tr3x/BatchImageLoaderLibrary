namespace BatchImageLoaderLibrary
{
	// Проверка «это вообще картинка?» по первым байтам — до декодирования и до
	// записи в кэш. Content-Type не используем: CDN-ы в нём регулярно врут,
	// а страница ошибки или капчи с кодом 200 — обычное дело.
	internal static class ImageSignature
	{
		private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF };
		private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
		private static readonly byte[] TiffLittleEndian = { 0x49, 0x49, 0x2A, 0x00 };
		private static readonly byte[] TiffBigEndian = { 0x4D, 0x4D, 0x00, 0x2A };
		private static readonly byte[] Ico = { 0x00, 0x00, 0x01, 0x00 };

		public static bool IsImage(ReadOnlySpan<byte> data)
		{
			// Короче 12 байт не бывает ни один реальный формат из списка.
			if (data.Length < 12)
				return false;

			return data.StartsWith(Jpeg)
				|| data.StartsWith(Png)
				|| data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)
				|| data.StartsWith("BM"u8)
				|| (data.StartsWith("RIFF"u8) && data.Slice(8).StartsWith("WEBP"u8))
				|| data.StartsWith(TiffLittleEndian) || data.StartsWith(TiffBigEndian)
				|| data.StartsWith(Ico)
				// HEIF/AVIF-контейнер: размер бокса, затем "ftyp".
				|| data.Slice(4).StartsWith("ftyp"u8);
		}
	}
}
