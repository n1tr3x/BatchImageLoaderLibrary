using BatchImageLoaderLibrary.DataProviders.Interfaces;
using Microsoft.Data.Sqlite;
using System.Data;

namespace BatchImageLoaderLibrary.DataProviders
{
	internal class SQLiteDataProvider : IDataProvider
	{
		private SqliteConnection DBConnection;

		// Вариант картинки (размер превью / "orig"), часть составного ключа.
		public string Variant { get; set; } = "orig";

		public SQLiteDataProvider(string dbName)
		{
			DBConnection = new SqliteConnection(@"Data Source=" + dbName);
			DBConnection.Open();
			EnsureSchema();
		}

		private void EnsureSchema()
		{
			// Версионируем схему. Всё, что старее v1 (включая таблицу старого
			// формата без колонки variant), просто сбрасываем — это кэш,
			// мигрировать данные не нужно.
			long version;
			using (SqliteCommand get = new SqliteCommand("PRAGMA user_version", DBConnection))
				version = (long)get.ExecuteScalar();

			if (version < 1)
			{
				new SqliteCommand("DROP TABLE IF EXISTS images", DBConnection).ExecuteNonQuery();
				new SqliteCommand("CREATE TABLE images (path TEXT, variant TEXT, data BLOB NOT NULL, PRIMARY KEY(path, variant))", DBConnection).ExecuteNonQuery();
				new SqliteCommand("PRAGMA user_version = 1", DBConnection).ExecuteNonQuery();
			}
		}

		public byte[] Get(string path)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();
			string sql = "SELECT data FROM Images WHERE path = @path AND variant = @variant";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@path", path);
			command.Parameters.AddWithValue("@variant", Variant);

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
			string sql = "SELECT path, data FROM Images WHERE variant = @variant";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@variant", Variant);
			SqliteDataReader reader = await command.ExecuteReaderAsync();

			while (await reader.ReadAsync())
			{
				result[(string)reader["path"]] = (byte[])reader["data"];
			}

			return result;
		}

		public void Add(string path, byte[] data)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();

			string sql = "INSERT OR REPLACE INTO Images (path, variant, data) VALUES(@path, @variant, @data)";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@path", path);
			command.Parameters.AddWithValue("@variant", Variant);
			command.Parameters.Add(@"data", SqliteType.Blob, data.Length).Value = data;
			command.ExecuteNonQuery();
		}

		public void Update(string path, byte[] data)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();

			string sql = "UPDATE Images SET data = @data WHERE path = @path AND variant = @variant";
			SqliteCommand command = new SqliteCommand(sql, DBConnection);
			command.Parameters.AddWithValue("@path", path);
			command.Parameters.AddWithValue("@variant", Variant);
			command.Parameters.Add(@"data", SqliteType.Blob, data.Length).Value = data;
			command.ExecuteNonQuery();
		}

		public void Remove(string path)
		{
			if (DBConnection.State != ConnectionState.Open)
				DBConnection.Open();

			// Удаляем ВСЕ варианты (размеры) этого URL, а не только текущий.
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
