namespace Hmi.Comms.Core;

/// <summary>
/// A device address after parsing. Two tags whose addresses have the same Key
/// on the same connection are the same physical point and are read once.
/// </summary>
public abstract class ParsedAddress
{
	/// <summary>Unique within a connection -- includes anything, like a Modbus unit id, that the
	/// connection itself does not.</summary>
	public abstract string Key { get; }

	/// <summary>The canonical spelling, shown back to the user.</summary>
	public abstract string Normalized { get; }

	/// <summary>The value type; Unknown until learned for protocols that report it.</summary>
	public abstract DataType Type { get; }

	public virtual bool Writable => true;

	/// <summary>Maximum string length, for string points.</summary>
	public virtual int? MaxLength => null;

	public override string ToString() => Normalized;
}

public readonly record struct ParseResult(ParsedAddress? Address, string? Error)
{
	public static ParseResult Ok(ParsedAddress a) => new(a, null);

	public static ParseResult Fail(string error) => new(null, error);
}

/// <summary>One protocol. Stateless; creates a connection per device.</summary>
public interface IProtocolDriver
{
	/// <summary>"logix", "slc", "modbus".</summary>
	string Protocol { get; }

	int DefaultPort { get; }

	/// <summary>Returns an error when the device settings are unusable, else null.</summary>
	string? ValidateDevice(DeviceConfig device);

	/// <summary>Devices with the same key share one connection (and one worker).</summary>
	string ConnectionKey(DeviceConfig device);

	ParseResult Parse(DeviceConfig device, string address, string? dataTypeHint);

	IDeviceConnection Create(DeviceConfig device);
}

/// <summary>
/// A connection to one device, used by exactly one worker thread. Methods are
/// synchronous because the protocol libraries are.
/// </summary>
public interface IDeviceConnection : IDisposable
{
	/// <summary>Opens the connection; throws on failure.</summary>
	void Connect();

	/// <summary>Builds a read plan for these points. Plans are cached by the worker and
	/// reused while the same set of points is due.</summary>
	IReadPlan Plan(IReadOnlyList<Point> points);

	/// <summary>Reads everything in the plan, reporting each point to the sink. Throws
	/// CommsLostException (or an IOException/SocketException) when the connection is gone;
	/// a point the device refuses is reported with sink.Fail instead.</summary>
	void Read(IReadPlan plan, IReadSink sink);

	/// <summary>Writes raw values, already coerced to each point's type.</summary>
	WriteOutcome[] Write(IReadOnlyList<WriteItem> items);
}

public interface IReadPlan
{
	/// <summary>Number of requests one pass of the plan makes.</summary>
	int RequestCount { get; }

	/// <summary>Human-readable description of each request, for diagnostics.</summary>
	IReadOnlyList<string> Describe();
}

public interface IReadSink
{
	void Set(Point point, object? raw);

	void Fail(Point point, string status, string error);
}

public readonly record struct WriteItem(Point Point, object? Raw);

public static class Errors
{
	/// <summary>
	/// The message worth showing a person: task and socket layers wrap the
	/// real failure ("One or more errors occurred. (Connection refused)").
	/// </summary>
	public static string Describe(Exception e)
	{
		while (e is AggregateException { InnerExceptions.Count: 1 } a)
		{
			e = a.InnerExceptions[0];
		}

		return e.Message;
	}
}

/// <summary>The connection is gone; the worker reconnects with backoff.</summary>
public sealed class CommsLostException : Exception
{
	public CommsLostException(string message, Exception? inner = null) : base(message, inner)
	{
	}
}
