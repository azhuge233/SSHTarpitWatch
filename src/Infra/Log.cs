using System.Globalization;

namespace SSHTarpitWatch.Infra;

/// <summary>日志级别：error &lt; info &lt; debug（数值越大越详细）。</summary>
internal enum LogLevel
{
	Error = 0,
	Info = 1,
	Debug = 2,
}

/// <summary>
/// 极简日志器（设计 §6）：写 stdout（systemd 下进 journald），行缓冲；
/// 格式 <c>{ISO8601 UTC 毫秒}Z {LEVEL} {事件} ...</c>。
/// </summary>
internal static class Log
{
	private static readonly object sync = new();
	private static int level = (int)LogLevel.Info;

	/// <summary>当前级别；SIGHUP 重载可即时改写。</summary>
	public static LogLevel Level
	{
		get => (LogLevel)Volatile.Read(ref level);
		set => Volatile.Write(ref level, (int)value);
	}

	public static void Error(string message) => Write(LogLevel.Error, message);

	public static void Info(string message) => Write(LogLevel.Info, message);

	public static void Debug(string message) => Write(LogLevel.Debug, message);

	/// <summary>低级别行直接丢弃；多线程下保证整行原子。</summary>
	private static void Write(LogLevel messageLevel, string message)
	{
		if (messageLevel > Level)
		{
			return;
		}

		string line = string.Concat(Timestamp(), " ", Label(messageLevel), " ", message);
		lock (sync)
		{
			Console.Out.WriteLine(line);
		}
	}

	/// <summary>形如 2026-10-06T02:09:36.123Z。</summary>
	private static string Timestamp() =>
		DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

	private static string Label(LogLevel messageLevel) => messageLevel switch
	{
		LogLevel.Error => "ERROR",
		LogLevel.Debug => "DEBUG",
		_ => "INFO",
	};
}
