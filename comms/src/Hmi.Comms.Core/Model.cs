namespace Hmi.Comms.Core;

/// <summary>Canonical value types a point can hold.</summary>
public enum DataType
{
	/// <summary>Not known until the device tells us (Logix, before the first read).</summary>
	Unknown,
	Bool,
	Int8,
	UInt8,
	Int16,
	UInt16,
	Int32,
	UInt32,
	Int64,
	UInt64,
	Float32,
	Float64,
	String
}

public static class DataTypes
{
	public static bool IsInteger(DataType t) => t is DataType.Int8 or DataType.UInt8 or
		DataType.Int16 or DataType.UInt16 or DataType.Int32 or DataType.UInt32 or
		DataType.Int64 or DataType.UInt64;

	public static bool IsFloat(DataType t) => t is DataType.Float32 or DataType.Float64;

	public static bool IsNumeric(DataType t) => IsInteger(t) || IsFloat(t);

	/// <summary>Parses a type hint as sent by clients: BOOL, INT, DINT, REAL, INT16, FLOAT, STRING...</summary>
	public static DataType? ParseHint(string? hint)
	{
		if (string.IsNullOrWhiteSpace(hint))
		{
			return null;
		}

		return hint.Trim().ToUpperInvariant() switch
		{
			"BOOL" or "BOOLEAN" or "BIT" or "DISCRETE" => DataType.Bool,
			"SINT" or "INT8" => DataType.Int8,
			"USINT" or "BYTE" or "UINT8" => DataType.UInt8,
			"INT" or "INT16" or "SHORT" => DataType.Int16,
			"UINT" or "WORD" or "UINT16" => DataType.UInt16,
			"DINT" or "INT32" or "INTEGER" => DataType.Int32,
			"UDINT" or "DWORD" or "UINT32" => DataType.UInt32,
			"LINT" or "INT64" => DataType.Int64,
			"ULINT" or "LWORD" or "UINT64" => DataType.UInt64,
			"REAL" or "FLOAT" or "FLOAT32" or "SINGLE" => DataType.Float32,
			"LREAL" or "DOUBLE" or "FLOAT64" => DataType.Float64,
			"STRING" or "MESSAGE" => DataType.String,
			_ => null
		};
	}
}

/// <summary>A device as a client describes it: a logical name, where it is and how to talk to it.</summary>
public sealed record DeviceConfig
{
	public required string Name { get; init; }
	public required string Protocol { get; init; }
	public string Host { get; init; } = "";
	public int? Port { get; init; }
	public int TimeoutMs { get; init; } = 3000;
	public int MinScanMs { get; init; } = 50;
	public bool Enabled { get; init; } = true;

	/// <summary>Protocol-specific settings, as strings; each driver validates its own.</summary>
	public IReadOnlyDictionary<string, string> Options { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public string? Option(string key)
	{
		return Options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
	}

	public int IntOption(string key, int fallback)
	{
		string? v = Option(key);

		return v != null && int.TryParse(v, out int n) ? n : fallback;
	}

	public bool BoolOption(string key, bool fallback)
	{
		string? v = Option(key);

		if (v == null)
		{
			return fallback;
		}

		return v.ToLowerInvariant() is "1" or "true" or "yes" or "on";
	}
}

/// <summary>Linear raw to engineering-unit scaling.</summary>
public sealed record Scaling(double RawMin, double RawMax, double EuMin, double EuMax, bool Clamp = false)
{
	public bool IsIdentity => RawMin == EuMin && RawMax == EuMax;

	public double ToEu(double raw)
	{
		if (RawMax == RawMin)
		{
			return EuMin;
		}

		double eu = EuMin + (raw - RawMin) * (EuMax - EuMin) / (RawMax - RawMin);

		return Clamp ? ClampTo(eu, EuMin, EuMax) : eu;
	}

	public double ToRaw(double eu)
	{
		if (EuMax == EuMin)
		{
			return RawMin;
		}

		double raw = RawMin + (eu - EuMin) * (RawMax - RawMin) / (EuMax - EuMin);

		return Clamp ? ClampTo(raw, RawMin, RawMax) : raw;
	}

	private static double ClampTo(double v, double a, double b)
	{
		return Math.Clamp(v, Math.Min(a, b), Math.Max(a, b));
	}
}

/// <summary>A tag as a client describes it: an id of its choosing bound to a device address.</summary>
public sealed record TagConfig
{
	public required string Id { get; init; }
	public required string Device { get; init; }
	public required string Address { get; init; }
	public string? DataType { get; init; }
	public double Deadband { get; init; }
	public Scaling? Scale { get; init; }
	public bool ReadOnly { get; init; }
}

/// <summary>OPC DA quality values, the scale the HMI already uses.</summary>
public static class Quality
{
	public const int Bad = 0;
	public const int Uncertain = 64;
	public const int Good = 192;
}

/// <summary>Why a value is bad.</summary>
public static class Status
{
	/// <summary>Not read yet.</summary>
	public const string Waiting = "waiting";

	/// <summary>The connection to the device is down.</summary>
	public const string Comm = "comm";

	/// <summary>The device refused this particular point.</summary>
	public const string Device = "device";

	/// <summary>The tag itself is wrong: bad address, unknown device.</summary>
	public const string Config = "config";

	/// <summary>The device is disabled.</summary>
	public const string Disabled = "disabled";
}

/// <summary>A value with its quality and the time it was read (UTC ms).</summary>
public readonly record struct TagValue(object? Value, int Quality, long Timestamp,
	string? Status = null, string? Error = null)
{
	public bool IsGood => Quality >= Core.Quality.Good;

	public static TagValue Bad(string status, string? error, long timestamp) =>
		new(null, Core.Quality.Bad, timestamp, status, error);

	public static TagValue Good(object? value, long timestamp) =>
		new(value, Core.Quality.Good, timestamp);

	/// <summary>Same value, quality and reason -- timestamps aside.</summary>
	public bool SameAs(TagValue other)
	{
		return Quality == other.Quality && Status == other.Status && Error == other.Error &&
			Equals(Value, other.Value);
	}
}

/// <summary>Result of one write.</summary>
public readonly record struct WriteOutcome(bool Ok, string? Error = null)
{
	public static readonly WriteOutcome Success = new(true);

	public static WriteOutcome Fail(string error) => new(false, error);
}
