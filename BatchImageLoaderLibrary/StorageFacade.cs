using BatchImageLoaderLibrary.DataProviders;
using BatchImageLoaderLibrary.DataProviders.Interfaces;

namespace BatchImageLoaderLibrary
{
	public enum StorageType
	{
		FILE,
		DB
	}

	// Выбирает бэкенд и держит пути к нему. Пути абсолютные и фиксируются
	// при создании провайдера; смена типа или пути пересоздаёт провайдер.
	internal class StorageFacade : IDataProvider
	{
		private StorageType storageType;
		private string cacheDirectory;
		private string databasePath;
		private IDataProvider dataProvider = null!;

		public StorageFacade(StorageType type, string cacheDirectory, string databasePath)
		{
			storageType = type;
			this.cacheDirectory = cacheDirectory;
			this.databasePath = databasePath;
			CreateStorage();
		}

		public StorageType StorageType
		{
			get => storageType;
			set
			{
				if (storageType == value)
					return;
				storageType = value;
				CreateStorage();
			}
		}

		public string CacheDirectory
		{
			get => cacheDirectory;
			set
			{
				if (cacheDirectory == value)
					return;
				cacheDirectory = value;
				if (storageType == StorageType.FILE)
					CreateStorage();
			}
		}

		public string DatabasePath
		{
			get => databasePath;
			set
			{
				if (databasePath == value)
					return;
				databasePath = value;
				if (storageType == StorageType.DB)
					CreateStorage();
			}
		}

		private void CreateStorage()
		{
			dataProvider = storageType switch
			{
				StorageType.FILE => new FilesystemDataProvider(cacheDirectory),
				_ => new SQLiteDataProvider(databasePath),
			};
		}

		public byte[]? Get(string url, string variant) => dataProvider.Get(url, variant);

		public Dictionary<string, byte[]> GetAll(string variant) => dataProvider.GetAll(variant);

		public void Add(string url, string variant, byte[] data) => dataProvider.Add(url, variant, data);

		public void Remove(string url) => dataProvider.Remove(url);

		public void RemoveAll() => dataProvider.RemoveAll();
	}
}
