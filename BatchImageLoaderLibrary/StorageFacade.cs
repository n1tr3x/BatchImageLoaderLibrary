using BatchImageLoaderLibrary.DataProviders;
using BatchImageLoaderLibrary.DataProviders.Interfaces;

namespace BatchImageLoaderLibrary
{
	public enum StorageType
	{
		FILE,
		DB
	}

	internal class StorageFacade : IDataProvider
	{
		private StorageType storageType;
		private string dbName = "BatchImageLoaderLibraryCache.sqlite";
		private string directoryName = @"cache";
        //private string directoryName = @"d:\dropbox\работа\PhotoSearchCommander\cache";

        private IDataProvider dataProvider;

		public StorageFacade(StorageType type)
		{
			storageType = type;
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
				
			}
		}

		private void CreateStorage()
		{
			switch (storageType)
			{
				case StorageType.FILE:
					dataProvider = new FilesystemDataProvider(directoryName);
					break;
				case StorageType.DB:
					dataProvider = new SQLiteDataProvider(DbName);
					break;
			}
		}

		public string DbName
		{
			get => dbName;
			set
			{
				if (storageType == StorageType.DB && dbName != value)
				{
					dataProvider = new SQLiteDataProvider(value);
				}
				dbName = value;
			}
		}

		public string DirectoryName
		{
			get => directoryName;
			set
			{
				directoryName = value;
				if (storageType == StorageType.FILE)
				{
					((FilesystemDataProvider)dataProvider).CacheDirectory = value;
				}
			}
		}

		public byte[] Get(string url)
		{
			//if (storageType == StorageType.FILE)
			//	url = NormalizeUrl(url);
			return dataProvider.Get(url);
		}

		public async Task<Dictionary<string, byte[]>> GetAll()
		{
			return await dataProvider.GetAll();
		}

		public void Add(string url, byte[] data)
		{
			//if (storageType == StorageType.FILE)
			//	url = NormalizeUrl(url);
			dataProvider.Add(url, data);
		}

		public void Update(string url, byte[] data)
		{
			//if (storageType == StorageType.FILE)
			//	url = NormalizeUrl(url);
			dataProvider.Update(url, data);
		}

		public void Remove(string url)
		{
			//if (storageType == StorageType.FILE)
			//	url = NormalizeUrl(url);
			dataProvider.Remove(url);
		}

		public void RemoveAll()
		{
			dataProvider.RemoveAll();
		}
	}
}