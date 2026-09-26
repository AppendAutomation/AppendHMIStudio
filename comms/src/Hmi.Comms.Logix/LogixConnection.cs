using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CSLogix;
using CSLogix.Models;
using Hmi.Comms.Core;

namespace Hmi.Comms.Logix;

/// <summary>
/// One tag read on the wire, and the points it feeds: the tag itself, bits of
/// it, or bools of a BOOL array that live in this DWORD.
/// </summary>
public sealed class LogixRead
{
	public required string Tag;
	public required string Key;
	public required int PathBytes;

	/// <summary>Each point, and the bit of the value it takes (-1 for the whole value).</summary>
	public readonly List<(Point Point, int Bit)> Targets = new();
}

public sealed class LogixPlan : IReadPlan
{
	public required IReadOnlyList<Point> Points;
	public List<LogixRead> Reads = new();
	public List<List<LogixRead>> Chunks = new();

	/// <summary>The learned-size generation the chunks were built for.</summary>
	public int SizeVersion = -1;

	/// <summary>The known-BOOL-array generation the reads were built for.</summary>
	public int BoolVersion = -1;

	public int RequestCount => Chunks.Count;

	public IReadOnlyList<string> Describe() =>
		Chunks.Select(c => $"{c.Count} tags: {string.Join(", ", c.Take(6).Select(r => r.Tag))}" +
			(c.Count > 6 ? ", ..." : "")).ToList();
}

/// <summary>
/// Packs reads into Multiple Service Packets. A request and its reply must
/// each fit the connection size the Forward Open negotiated; request sizes are
/// known from the tag paths, reply sizes are learned from earlier reads and
/// assumed to be a STRING (the largest scalar) until then.
/// </summary>
public static class LogixChunker
{
	public const int UnknownReplyBytes = 88;
	private const int Margin = 16;

	/// <summary>Service, path size, 2-word Message Router path, service count.</summary>
	private const int RequestHeader = 8;

	/// <summary>Reply service, reserved, status, extended status size, reply count.</summary>
	private const int ReplyHeader = 6;

	public static int RequestCost(LogixRead r) => 2 + 2 + r.PathBytes + 2;

	public static int ReplyCost(int dataBytes) => 2 + 4 + 2 + dataBytes;

	public static List<List<LogixRead>> Chunk(IReadOnlyList<LogixRead> reads, int connectionSize,
		Func<string, int?> learnedBytes)
	{
		int budget = connectionSize - Margin;
		var chunks = new List<List<LogixRead>>();
		var current = new List<LogixRead>();
		int request = RequestHeader, reply = ReplyHeader;

		foreach (var r in reads)
		{
			int rq = RequestCost(r);
			int rp = ReplyCost(learnedBytes(r.Key) ?? UnknownReplyBytes);

			if (current.Count > 0 && (request + rq > budget || reply + rp > budget))
			{
				chunks.Add(current);
				current = new List<LogixRead>();
				request = RequestHeader;
				reply = ReplyHeader;
			}

			current.Add(r);
			request += rq;
			reply += rp;
		}

		if (current.Count > 0)
		{
			chunks.Add(current);
		}

		return chunks;
	}
}

public sealed class LogixConnection : IDeviceConnection
{
	private const string TooLarge = "Reply data too large";

	private readonly DeviceConfig device;
	private readonly Dictionary<string, int> replyBytes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, byte> types = new(StringComparer.Ordinal);

	// Arrays found to be BOOL arrays (packed in DWORDs), and arrays already checked.
	private readonly HashSet<string> boolArrays = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> probedArrays = new(StringComparer.OrdinalIgnoreCase);
	private PLC? plc;
	private int sizeVersion;
	private int boolVersion;

	public LogixConnection(DeviceConfig device)
	{
		this.device = device;
	}

	public void Connect()
	{
		var ip = Resolve(device.Host);

		plc = new PLC(ip.ToString())
		{
			Port = device.Port ?? LogixDriver.Port,
			ProcessorSlot = device.IntOption("slot", 0),
			Micro800 = device.BoolOption("micro800", false),
			SocketTimeout = Math.Max(0.5, device.TimeoutMs / 1000.0)
		};

		var r = plc.Connect();

		if (!r.IsSuccess)
		{
			throw new IOException(r.Status);
		}
	}

	private static IPAddress Resolve(string host)
	{
		if (IPAddress.TryParse(host, out var ip))
		{
			return ip;
		}

		var all = Dns.GetHostAddresses(host);

		return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
	}

	private PLC Plc => plc ?? throw new CommsLostException("Not connected");

	public int ConnectionSize => plc?.NegotiatedConnectionSize ?? 504;

	public IReadPlan Plan(IReadOnlyList<Point> points)
	{
		return new LogixPlan { Points = points };
	}

	/// <summary>What is read for a point: its own tag, or the DWORD of a BOOL array holding it.</summary>
	private (string Tag, int Bit, int PathBytes) Resolve(LogixAddress a)
	{
		if (a.TrailingIndex is (string array, int index) && boolArrays.Contains(array))
		{
			string tag = $"{array}[{index / 32}]";

			return (tag, index % 32, ((LogixAddress)LogixAddress.Parse(tag).Address!).PathBytes);
		}

		return (a.ReadTag, a.Bit, a.PathBytes);
	}

	private List<LogixRead> BuildReads(IReadOnlyList<Point> points)
	{
		var reads = new Dictionary<string, LogixRead>(StringComparer.Ordinal);

		foreach (var p in points)
		{
			var (tag, bit, pathBytes) = Resolve((LogixAddress)p.Address);
			string key = tag.ToUpperInvariant();

			if (!reads.TryGetValue(key, out var r))
			{
				reads[key] = r = new LogixRead { Tag = tag, Key = key, PathBytes = pathBytes };
			}

			r.Targets.Add((p, bit));
		}

		return reads.Values.ToList();
	}

	public void Read(IReadPlan p, IReadSink sink)
	{
		var plan = (LogixPlan)p;

		if (plan.BoolVersion != boolVersion)
		{
			plan.Reads = BuildReads(plan.Points);
			plan.BoolVersion = boolVersion;
			plan.SizeVersion = -1;
		}

		if (plan.SizeVersion != sizeVersion)
		{
			plan.Chunks = LogixChunker.Chunk(plan.Reads, ConnectionSize, Learned);
			plan.SizeVersion = sizeVersion;
		}

		foreach (var chunk in plan.Chunks.ToList())
		{
			ReadChunk(plan, chunk, sink);
		}
	}

	private int? Learned(string key) => replyBytes.TryGetValue(key, out var n) ? n : null;

	private void ReadChunk(LogixPlan plan, List<LogixRead> chunk, IReadSink sink)
	{
		var responses = Plc.Read(chunk.Select(r => r.Tag).ToList());

		if (!Plc.IsConnected)
		{
			throw new CommsLostException(responses.FirstOrDefault()?.Status ?? "Connection lost");
		}

		// The whole packet was refused as too big for the connection: our size
		// guess was wrong. Split it and try the halves.
		if (chunk.Count > 1 && responses.All(r => r.Status == TooLarge))
		{
			int at = plan.Chunks.IndexOf(chunk);
			int half = chunk.Count / 2;
			var a = chunk.Take(half).ToList();
			var b = chunk.Skip(half).ToList();

			if (at >= 0)
			{
				plan.Chunks[at] = a;
				plan.Chunks.Insert(at + 1, b);
			}

			ReadChunk(plan, a, sink);
			ReadChunk(plan, b, sink);

			return;
		}

		for (int i = 0; i < chunk.Count && i < responses.Count; i++)
		{
			var read = chunk[i];
			var resp = responses[i];

			if (!resp.IsSuccess)
			{
				// Bits[37] is refused on a BOOL[64]: its element numbers count
				// DWORDs. Check once whether that is what this array is; if so,
				// the next pass reads the right DWORD.
				if (resp.Status == "Path destination unknown")
				{
					LearnBoolArray(read);
				}

				foreach (var (point, _) in read.Targets)
				{
					sink.Fail(point, Status.Device, resp.Status);
				}

				continue;
			}

			// A DWORD from Name[i] means Name is a BOOL array and this is a whole
			// DWORD, not bool i. Remember it; the next pass reads the right bit.
			if (resp.CipType == CipType.DWORD && read.Targets.Any(t => t.Bit < 0 &&
				((LogixAddress)t.Point.Address).TrailingIndex is (string array, _) && boolArrays.Add(array)))
			{
				boolVersion++;

				continue;
			}

			Learn(read.Key, resp);
			Deliver(read, resp, sink);
		}
	}

	private void LearnBoolArray(LogixRead read)
	{
		foreach (var (point, _) in read.Targets)
		{
			if (((LogixAddress)point.Address).TrailingIndex is (string array, _) && probedArrays.Add(array))
			{
				var probe = Plc.Read(array + "[0]");

				if (!Plc.IsConnected)
				{
					throw new CommsLostException(probe.Status);
				}

				if (probe.IsSuccess && probe.CipType == CipType.DWORD)
				{
					boolArrays.Add(array);
					boolVersion++;
				}
			}
		}
	}

	/// <summary>Remembers the type and reply size the controller reported for a tag.</summary>
	private void Learn(string key, Response resp)
	{
		if (resp.CipType is not byte type)
		{
			return;
		}

		types[key] = type;
		int bytes = type == CipType.STRUCT ? LogixChunker.UnknownReplyBytes : CipType.Size(type);

		if (!replyBytes.TryGetValue(key, out int before) || before != bytes)
		{
			replyBytes[key] = bytes;
			sizeVersion++;
		}
	}

	private static void Deliver(LogixRead read, Response resp, IReadSink sink)
	{
		object? value = Canonical(resp.Value);

		foreach (var (point, bit) in read.Targets)
		{
			if (bit < 0)
			{
				sink.Set(point, value);
			}
			else if (value is long whole)
			{
				sink.Set(point, ((whole >> bit) & 1) != 0);
			}
			else if (value is ulong uwhole)
			{
				sink.Set(point, ((uwhole >> bit) & 1) != 0);
			}
			else if (value is bool b && bit == 0)
			{
				sink.Set(point, b);
			}
			else
			{
				sink.Fail(point, Status.Device, $"{read.Tag} is not an integer");
			}
		}
	}

	/// <summary>The library's CLR values in the server's canonical forms.</summary>
	internal static object? Canonical(object? v) => v switch
	{
		null => null,
		bool b => b,
		sbyte x => (long)x,
		byte x => (long)x,
		short x => (long)x,
		ushort x => (long)x,
		int x => (long)x,
		uint x => (long)x,
		long x => x,
		ulong x => x,
		// A REAL is shown as its shortest decimal: 0.1, not 0.100000001490116.
		float f => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
		double d => d,
		string s => s,
		_ => v.ToString()
	};

	public WriteOutcome[] Write(IReadOnlyList<WriteItem> items)
	{
		return items.Select(WriteOne).ToArray();
	}

	private WriteOutcome WriteOne(WriteItem item)
	{
		var a = (LogixAddress)item.Point.Address;
		byte? known = types.TryGetValue(a.ReadKey, out var t) ? t : null;

		// One bool of a BOOL array: an atomic bit write to the DWORD holding it.
		if (a.TrailingIndex is (string array, int index) && boolArrays.Contains(array))
		{
			return Outcome(Plc.Write($"{array}[{index / 32}].{index % 32}", Convert.ToBoolean(item.Raw),
				CipType.DWORD));
		}

		if (a.Bit >= 0)
		{
			var r = Plc.Write(a.Text, Convert.ToBoolean(item.Raw), known);

			return Outcome(r);
		}

		// Writes use the type the controller reported, read first if need be:
		// sending a DINT to an INT tag is refused as a type mismatch.
		if (known == null)
		{
			var probe = Plc.Read(a.ReadTag);

			if (!Plc.IsConnected)
			{
				throw new CommsLostException(probe.Status);
			}

			if (!probe.IsSuccess || probe.CipType == null)
			{
				return WriteOutcome.Fail(probe.IsSuccess ? "Cannot tell the tag's type" : probe.Status);
			}

			Learn(a.ReadKey, probe);
			known = probe.CipType;
		}

		var dataType = CipType.ToDataType(known.Value);

		if (dataType == null)
		{
			return WriteOutcome.Fail("Writing this data type is not supported");
		}

		if (!ValueCodec.TryCoerce(item.Raw, dataType.Value, 82, out var coerced, out var error))
		{
			return WriteOutcome.Fail(error!);
		}

		return Outcome(Plc.Write(a.Text, CipType.ToClr(coerced!, dataType.Value), known));
	}

	private WriteOutcome Outcome(Response r)
	{
		if (!Plc.IsConnected)
		{
			throw new CommsLostException(r.Status);
		}

		return r.IsSuccess ? WriteOutcome.Success : WriteOutcome.Fail(r.Status);
	}

	public void Dispose()
	{
		plc?.Dispose();
		plc = null;
	}
}

/// <summary>CIP elementary type codes as Logix uses them.</summary>
public static class CipType
{
	public const byte BOOL = 0xC1, SINT = 0xC2, INT = 0xC3, DINT = 0xC4, LINT = 0xC5, USINT = 0xC6,
		UINT = 0xC7, UDINT = 0xC8, ULINT = 0xC9, REAL = 0xCA, LREAL = 0xCB, BYTE = 0xD1, WORD = 0xD2,
		DWORD = 0xD3, LWORD = 0xD4, STRUCT = 0xA0;

	public static int Size(byte t) => t switch
	{
		BOOL or SINT or USINT or BYTE => 1,
		INT or UINT or WORD => 2,
		DINT or UDINT or REAL or DWORD => 4,
		LINT or ULINT or LREAL or LWORD => 8,
		_ => LogixChunker.UnknownReplyBytes
	};

	public static DataType? ToDataType(byte t) => t switch
	{
		BOOL => DataType.Bool,
		SINT => DataType.Int8,
		USINT or BYTE => DataType.UInt8,
		INT => DataType.Int16,
		UINT or WORD => DataType.UInt16,
		DINT => DataType.Int32,
		UDINT or DWORD => DataType.UInt32,
		LINT => DataType.Int64,
		ULINT or LWORD => DataType.UInt64,
		REAL => DataType.Float32,
		LREAL => DataType.Float64,
		STRUCT => DataType.String,
		_ => null
	};

	/// <summary>A coerced value (bool, long, ulong, double, string) as the CLR type the library encodes.</summary>
	public static object ToClr(object v, DataType t) => t switch
	{
		DataType.Bool => (bool)v,
		DataType.Int8 => (sbyte)(long)v,
		DataType.UInt8 => (byte)(long)v,
		DataType.Int16 => (short)(long)v,
		DataType.UInt16 => (ushort)(long)v,
		DataType.Int32 => (int)(long)v,
		DataType.UInt32 => (uint)(long)v,
		DataType.Int64 => (long)v,
		DataType.UInt64 => v is ulong u ? u : (ulong)(long)v,
		DataType.Float32 => (float)(double)v,
		DataType.Float64 => (double)v,
		_ => (string)v
	};
}
