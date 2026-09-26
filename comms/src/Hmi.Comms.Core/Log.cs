namespace Hmi.Comms.Core;

public enum LogLevel
{
	Debug = 0,
	Info = 1,
	Warn = 2,
	Error = 3
}

/// <summary>
/// Minimal logger. Everything goes to stderr (stdout is reserved for the one
/// startup line the parent process reads) and optionally to a file.
///
/// One line per event worth knowing about -- a device changing state, a
/// session opening -- never one per poll.
/// </summary>
public static class Log
{
	private static readonly object Gate = new();
	private static StreamWriter? file;

	public static LogLevel Level { get; set; } = LogLevel.Info;

	public static void Configure(string level, string? path)
	{
		Level = level.ToLowerInvariant() switch
		{
			"debug" => LogLevel.Debug,
			"warn" or "warning" => LogLevel.Warn,
			"error" => LogLevel.Error,
			_ => LogLevel.Info
		};

		if (!string.IsNullOrEmpty(path))
		{
			file = new StreamWriter(path, append: true) { AutoFlush = true };
		}
	}

	public static void Debug(string message) => Write(LogLevel.Debug, message);
	public static void Info(string message) => Write(LogLevel.Info, message);
	public static void Warn(string message) => Write(LogLevel.Warn, message);
	public static void Error(string message) => Write(LogLevel.Error, message);

	private static void Write(LogLevel level, string message)
	{
		if (level < Level)
		{
			return;
		}

		string line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {Tag(level)} {message}";

		lock (Gate)
		{
			Console.Error.WriteLine(line);
			file?.WriteLine(line);
		}
	}

	private static string Tag(LogLevel level) => level switch
	{
		LogLevel.Debug => "DBG",
		LogLevel.Info => "INF",
		LogLevel.Warn => "WRN",
		_ => "ERR"
	};
}
