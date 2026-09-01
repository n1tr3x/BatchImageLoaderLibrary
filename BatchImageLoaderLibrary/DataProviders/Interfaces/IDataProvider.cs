namespace BatchImageLoaderLibrary.DataProviders.Interfaces
{
	// Персистентное хранилище кэша. Вариант картинки («120x120» или «orig»)
	// передаётся явно в каждую операцию: у провайдера нет разделяемого
	// изменяемого состояния, поэтому его безопасно дёргать из десятков потоков.
	internal interface IDataProvider
	{
		public byte[]? Get(string key, string variant);

		// Все записи одного варианта: url -> данные.
		public Dictionary<string, byte[]> GetAll(string variant);

		public void Add(string key, string variant, byte[] data);

		// Удаляет ВСЕ варианты ключа.
		public void Remove(string key);

		public void RemoveAll();
	}
}
