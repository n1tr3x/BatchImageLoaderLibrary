using BatchImageLoaderLibrary.DataProviders.Interfaces;
using Microsoft.Data.Sqlite;
using System.Data;

namespace BatchImageLoaderLibrary.DataProviders
{
	internal class SQLiteDataProvider : IDataProvider
	{
		private SqliteConnection DBConnection;

		public SQLiteDataProvider(string dbName)
		{
			if (!File.Exists(dbName))
			{
				DBConnection = new SqliteConnection(@"Data Source=" + dbName);
				DBConnection.Open();
				string sql = "CREATE TABLE IF NOT EXISTS images (path TEXT PRIMARY KEY, data BLOB NOT NULL)";
				SqliteCommand command = new SqliteCommand(sql, DBConnection);
				command.ExecuteNonQuery();
			}
			else
			{
				DBConnection = new SqliteConnection(@"Data Source=" + dbName);
			}
		}

		public byte[] Get(string path)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();
			string sql = "SELECT data FROM Images WHERE path = @path";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@path", path);

			SqliteDataReader reader = command.ExecuteReader();

			if (!reader.Read())
				return null;

			return (byte[])reader["data"];
		}

		public async Task<Dictionary<string, byte[]>> GetAll()
		{
			Dictionary<string, byte[]> result = new Dictionary<string, byte[]>();
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();
			string sql = "SELECT * FROM Images";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			SqliteDataReader reader = await command.ExecuteReaderAsync();

			while (await reader.ReadAsync())
			{
				result.Add((string)reader["path"], (byte[])reader["data"]);
			}

			return result;
		}

		public void Add(string path, byte[] data)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();

			string sql = "INSERT INTO Images (path, data) VALUES(@path, @data)";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@path", path);
			command.Parameters.Add(@"data", SqliteType.Blob, data.Length).Value = data;
			command.ExecuteNonQuery();
		}

		public void Update(string path, byte[] data)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();

			string sql = "UPDATE Images SET data = @data WHERE path = @path";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@path", path);
			command.Parameters.Add(@"data", SqliteType.Blob, data.Length).Value = data;
			command.ExecuteNonQuery();
		}

		public void Remove(string path)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();

			string sql = "DELETE FROM Images WHERE path = @path";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@path", path);
			command.ExecuteNonQuery();
			Flush();
		}

		public void RemoveAll()
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();

			string sql = "DELETE FROM Images";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.ExecuteNonQuery();
			Flush();
		}

		public void Flush()
		{
			string sql = "VACUUM";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.ExecuteNonQuery();
		}
	}
}
