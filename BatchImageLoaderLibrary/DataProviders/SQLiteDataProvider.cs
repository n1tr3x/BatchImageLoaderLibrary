using BatchImageLoaderLibrary.DataProviders.Interfaces;
using System.IO;
using Microsoft.Data.Sqlite;

namespace BatchImageLoaderLibrary.DataProviders
{
	internal class SQLiteDataProvider : IDataProvider
	{
		// Храним строку подключения, а не одно соединение: на каждую операцию
		// берём отдельное соединение из пула. SqliteConnection/SqliteCommand НЕ
		// потокобезопасны, а провайдер вызывают из десятков потоков сразу.
		private readonly string connectionString;

		public SQLiteDataProvider(string databasePath)
		{
			string? directory = Path.GetDirectoryName(databasePath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
			FileLog.Write("sqlite : open " + databasePath);
			using SqliteConnection connection = OpenConnection();
			EnsureSchema(connection);
		}

		// Пул включён по умолчанию (Microsoft.Data.Sqlite 6+), поэтому
		// Open/Dispose дёшевы — реально переиспользуется хэндл из пула.
		// busy_timeout заставляет ждать снятия блокировки вместо мгновенного
		// "database is locked" при конкурентной записи.
		private SqliteConnection OpenConnection()
		{
			SqliteConnection connection = new SqliteConnection(connectionString);
			connection.Open();
			Execute(connection, "PRAGMA busy_timeout = 30000");
			return connection;
		}

		private void EnsureSchema(SqliteConnection connection)
		{
			// WAL: конкурентные читатели не блокируют писателя и наоборот —
			// важно для пакетной загрузки. Режим персистентный, ставится один раз.
			Execute(connection, "PRAGMA journal_mode = WAL");

			// Версионируем схему. Всё, что старее v1 (включая таблицу старого
			// формата без колонки variant), просто сбрасываем — это кэш,
			// мигрировать данные не нужно.
			long version;
			using (SqliteCommand get = connection.CreateCommand())
			{
				get.CommandText = "PRAGMA user_version";
				version = Convert.ToInt64(get.ExecuteScalar());
			}

			if (version < 1)
			{
				FileLog.Write("sqlite : schema v" + version + " -> v1 (reset table)");
				Execute(connection, "DROP TABLE IF EXISTS images");
				Execute(connection, "CREATE TABLE images (path TEXT, variant TEXT, data BLOB NOT NULL, PRIMARY KEY(path, variant))");
				Execute(connection, "PRAGMA user_version = 1");
			}
			else
			{
				FileLog.Write("sqlite : schema v" + version + " ok (WAL)");
			}
		}

		private static void Execute(SqliteConnection connection, string sql)
		{
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = sql;
			command.ExecuteNonQuery();
		}

		public byte[]? Get(string path, string variant)
		{
			using SqliteConnection connection = OpenConnection();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT data FROM images WHERE path = @path AND variant = @variant";
			command.Parameters.AddWithValue("@path", path);
			command.Parameters.AddWithValue("@variant", variant);

			using SqliteDataReader reader = command.ExecuteReader();
			return reader.Read() ? (byte[])reader["data"] : null;
		}

		public Dictionary<string, byte[]> GetAll(string variant)
		{
			Dictionary<string, byte[]> result = new Dictionary<string, byte[]>();
			using SqliteConnection connection = OpenConnection();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT path, data FROM images WHERE variant = @variant";
			command.Parameters.AddWithValue("@variant", variant);

			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
				result[(string)reader["path"]] = (byte[])reader["data"];

			return result;
		}

		public void Add(string path, string variant, byte[] data)
		{
			using SqliteConnection connection = OpenConnection();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "INSERT OR REPLACE INTO images (path, variant, data) VALUES(@path, @variant, @data)";
			command.Parameters.AddWithValue("@path", path);
			command.Parameters.AddWithValue("@variant", variant);
			command.Parameters.Add("@data", SqliteType.Blob, data.Length).Value = data;
			command.ExecuteNonQuery();
		}

		public void Remove(string path)
		{
			// Удаляем ВСЕ варианты (размеры) этого URL, а не только текущий.
			using SqliteConnection connection = OpenConnection();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "DELETE FROM images WHERE path = @path";
			command.Parameters.AddWithValue("@path", path);
			command.ExecuteNonQuery();
		}

		public void RemoveAll()
		{
			using SqliteConnection connection = OpenConnection();
			Execute(connection, "DELETE FROM images");
			Execute(connection, "VACUUM");
		}
	}
}
