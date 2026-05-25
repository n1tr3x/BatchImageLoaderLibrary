namespace BatchImageLoaderLibrary.DataProviders.Interfaces
{
	internal interface IDataProvider
	{
		public byte[] Get(string key);

		public Task<Dictionary<string, byte[]>> GetAll();

		public void Add(string key, byte[] data);

		public void Update(string key, byte[] data);

		public void Remove(string key);

		public void RemoveAll();
	}
}