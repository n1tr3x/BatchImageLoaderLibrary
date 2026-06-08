namespace BatchImageLoaderLibrary.DataProviders.Interfaces
{
	internal interface IDataProvider
	{
		// Вариант кэшируемой картинки (размер превью "120x120" или "orig").
		// Входит в ключ кэша: имя файла у FILE, часть составного PK у DB.
		public string Variant { get; set; }

		public byte[] Get(string key);

		public Task<Dictionary<string, byte[]>> GetAll();

		public void Add(string key, byte[] data);

		public void Update(string key, byte[] data);

		public void Remove(string key);

		public void RemoveAll();
	}
}