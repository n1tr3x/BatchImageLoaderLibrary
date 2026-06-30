using System;
using System.IO;
using System.Threading;

namespace BatchImageLoaderLibrary
{
	// Опциональный детальный лог в файл. По умолчанию ВЫКЛЮЧЕН.
	// Включается через BatchImageLoader.LogFile = "путь". Потокобезопасен
	// (один writer под lock, AutoFlush — чтобы не терять строки при падении).
	// Рассчитан на диагностику: при включении пишет каждый шаг и заметно
	// замедляет работу под высокой конкуренцией — это нормально.
	internal static class FileLog
	{
		private static readonly object gate = new();
		private static StreamWriter? writer;
		private static string? path;

		// Быстрая проверка без захвата lock — чтобы дешёво пропускать
		// логирование (и не собирать строки), когда оно выключено.
		public static bool Enabled => writer != null;

		public static string? Path => path;

		public static void Configure(string? filePath)
		{
			lock (gate)
			{
				if (writer != null)
				{
					WriteLocked("=== logging stopped ===");
					writer.Flush();
					writer.Dispose();
					writer = null;
					path = null;
				}

				if (string.IsNullOrWhiteSpace(filePath))
					return;

				string full = System.IO.Path.GetFullPath(filePath);
				string? dir = System.IO.Path.GetDirectoryName(full);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);

				writer = new StreamWriter(full, append: true) { AutoFlush = true };
				path = full;
				WriteLocked("=== logging started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
					" (pid " + Environment.ProcessId + ") ===");
			}
		}

		public static void Write(string message)
		{
			if (writer == null)
				return;
			lock (gate)
			{
				if (writer == null)
					return;
				WriteLocked(message);
			}
		}

		private static void WriteLocked(string message)
		{
			writer!.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") +
				" [t" + Environment.CurrentManagedThreadId.ToString("D2") + "] " + message);
		}
	}
}
