using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CSComm3.SLC;
using CSComm3.SLC.Exceptions;
using Hmi.Comms.Core;

namespace Hmi.Comms.Slc;

public sealed record SlcLimits(int MaxBytesPerRequest = SLCDriver.MaxRawBytes, int MaxGapElements = 118);

/// <summary>One request: a run of elements of one data file, or one I/O word.</summary>
public sealed class SlcBlock
{
	public required byte FileType;
	public required int File;
	public required int StartElement;
	public int Elements = 1;

	/// <summary>Word offset for an I/O read; data-file blocks start at word 0.</summary>
	public int Word;

	public bool IO;
	public readonly List<Point> Points = new();
	public long RetryAfter;

	public int ElementBytes => SlcAddress.ElementSize(FileType);

	public int ByteCount => IO ? 2 : Elements * ElementBytes;

	public string Describe() => IO ?
		$"file {File} type 0x{FileType:X2} slot {StartElement} word {Word}" :
		$"file {File} type 0x{FileType:X2} elements {StartElement}..{StartElement + Elements - 1} ({ByteCount} bytes, {Points.Count} points)";
}

public sealed class SlcPlan : IReadPlan
{
	public required IReadOnlyList<Point> Points;
	public required List<SlcBlock> Blocks;
	public long SplitAt;

	public int RequestCount => Blocks.Count;

	public IReadOnlyList<string> Describe() => Blocks.Select(b => b.Describe()).ToList();
}

/// <summary>
/// Coalesces points of one data file into runs of elements that fit one
/// typed read (about 236 bytes: 118 integers, 59 floats, 39 timers, 2
/// strings), bridging holes of up to MaxGapElements.
/// </summary>
public static class SlcBlockBuilder
{
	/// <param name="barriers">Elements the processor refused, as (file type, file, element):
	/// a run may not reach across one.</param>
	public static List<SlcBlock> Build(IEnumerable<Point> points, SlcLimits limits,
		ISet<(byte, int, int)>? barriers = null)
	{
		var blocks = new List<SlcBlock>();
		var list = points.ToList();

		foreach (var p in list.Where(p => ((SlcAddress)p.Address).IsIO))
		{
			var a = (SlcAddress)p.Address;
			var b = new SlcBlock { FileType = a.FileType, File = a.FileNumber, StartElement = a.Element, Word = a.Word, IO = true };
			b.Points.Add(p);
			blocks.Add(b);
		}

		var groups = list.Where(p => !((SlcAddress)p.Address).IsIO)
			.GroupBy(p => (((SlcAddress)p.Address).FileType, ((SlcAddress)p.Address).FileNumber))
			.OrderBy(g => g.Key.FileNumber).ThenBy(g => g.Key.FileType);

		foreach (var g in groups)
		{
			int size = SlcAddress.ElementSize(g.Key.FileType);
			int maxElements = Math.Max(1, limits.MaxBytesPerRequest / size);
			SlcBlock? block = null;

			foreach (var p in g.OrderBy(p => ((SlcAddress)p.Address).Element))
			{
				int el = ((SlcAddress)p.Address).Element;

				if (block != null && el <= block.StartElement + block.Elements + limits.MaxGapElements &&
					Math.Max(block.StartElement + block.Elements - 1, el) - block.StartElement + 1 <= maxElements &&
					!Crosses(barriers, g.Key.FileType, g.Key.FileNumber, block.StartElement + block.Elements - 1, el))
				{
					block.Elements = Math.Max(block.Elements, el - block.StartElement + 1);
					block.Points.Add(p);

					continue;
				}

				block = new SlcBlock { FileType = g.Key.FileType, File = g.Key.FileNumber, StartElement = el };
				block.Points.Add(p);
				blocks.Add(block);
			}
		}

		return blocks;
	}

	private static bool Crosses(ISet<(byte, int, int)>? barriers, byte type, int file, int from, int to)
	{
		if (barriers == null || barriers.Count == 0)
		{
			return false;
		}

		for (int e = from + 1; e < to; e++)
		{
			if (barriers.Contains((type, file, e)))
			{
				return true;
			}
		}

		return false;
	}
}

public sealed class SlcConnection : IDeviceConnection
{
	public const int RetryRefusedMs = 60_000;
	public const int RemergeMs = 10 * 60_000;

	private readonly DeviceConfig device;
	private readonly SlcLimits limits;
	private readonly bool swapStrings;
	private readonly Func<long> ticks;
	private SLCDriver? plc;

	public SlcConnection(DeviceConfig device, SlcLimits limits, Func<long>? ticks = null)
	{
		this.device = device;
		this.limits = limits;
		// SLC processors keep ST and A characters with the bytes of each word
		// swapped (checked against a 5/05 and pycomm3); the option is there for
		// a device that does not.
		swapStrings = device.BoolOption("swapStringBytes", true);
		this.ticks = ticks ?? (() => Environment.TickCount64);
	}

	public void Connect()
	{
		var ip = Resolve(device.Host);
		int port = device.Port ?? SlcDriver.Port;
		string path = port == SlcDriver.Port ? ip.ToString() : $"{ip}:{port}";

		try
		{
			plc = new SLCDriver(path) { Timeout = device.TimeoutMs };
			plc.Open();
		}
		catch (Exception e) when (e is not IOException)
		{
			plc?.Dispose();
			plc = null;

			throw new IOException(e.Message, e);
		}
	}

	private static IPAddress Resolve(string host)
	{
		if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
		{
			return ip;
		}

		return Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
	}

	private SLCDriver Plc
	{
		get
		{
			if (plc == null || !plc.Connected)
			{
				throw new CommsLostException("Not connected");
			}

			return plc;
		}
	}

	public IReadPlan Plan(IReadOnlyList<Point> points)
	{
		return new SlcPlan { Points = points, Blocks = SlcBlockBuilder.Build(points, limits) };
	}

	public void Read(IReadPlan p, IReadSink sink)
	{
		var plan = (SlcPlan)p;
		long now = ticks();

		if (plan.SplitAt != 0 && now - plan.SplitAt > RemergeMs)
		{
			plan.Blocks = SlcBlockBuilder.Build(plan.Points, limits);
			plan.SplitAt = 0;
		}

		var result = new List<SlcBlock>(plan.Blocks.Count);
		bool split = false;

		foreach (var block in plan.Blocks)
		{
			split |= ReadBlock(plan, block, sink, result, now);
		}

		// Splitting isolates what the processor refused but leaves the rest in
		// pieces; merge the good points back, without reaching over a refusal.
		plan.Blocks = split ? Remerge(result, now) : result;
	}

	private List<SlcBlock> Remerge(List<SlcBlock> blocks, long now)
	{
		var refused = blocks.Where(b => b.RetryAfter > now).ToList();
		var barriers = new HashSet<(byte, int, int)>(refused.Select(b => (b.FileType, b.File, b.StartElement)));
		var merged = SlcBlockBuilder.Build(blocks.Where(b => b.RetryAfter <= now).SelectMany(b => b.Points),
			limits, barriers);
		merged.AddRange(refused);

		return merged;
	}

	/// <returns>True when the block had to be split.</returns>
	private bool ReadBlock(SlcPlan plan, SlcBlock block, IReadSink sink, List<SlcBlock> result, long now)
	{
		if (block.RetryAfter > now)
		{
			result.Add(block);

			return false;
		}

		byte[] data;

		try
		{
			data = Guard(() => Plc.ReadRaw(block.FileType, block.File, block.StartElement,
				block.IO ? block.Word : 0, block.ByteCount));
		}
		catch (ResponseException e)
		{
			// The processor refused the range: most likely it runs past the
			// end of the file. Halve it until only the point at fault is left.
			if (block.Points.Count > 1)
			{
				plan.SplitAt = now;
				int half = block.Points.Count / 2;

				foreach (var part in new[] { block.Points.Take(half), block.Points.Skip(half) })
				{
					foreach (var sub in SlcBlockBuilder.Build(part, limits))
					{
						ReadBlock(plan, sub, sink, result, now);
					}
				}

				return true;
			}

			sink.Fail(block.Points[0], Status.Device, e.Message);
			block.RetryAfter = now + RetryRefusedMs;
			result.Add(block);

			return false;
		}

		foreach (var point in block.Points)
		{
			var a = (SlcAddress)point.Address;
			int offset = block.IO ? 0 : (a.Element - block.StartElement) * block.ElementBytes + a.Word * 2;

			if (offset + a.ValueBytes > data.Length)
			{
				sink.Fail(point, Status.Device, "Reply shorter than requested");

				continue;
			}

			sink.Set(point, Decode(a, data.AsSpan(offset, a.ValueBytes)));
		}

		block.RetryAfter = 0;
		result.Add(block);

		return false;
	}

	/// <summary>Data-table bytes (little-endian words) to a value.</summary>
	internal object? Decode(SlcAddress a, ReadOnlySpan<byte> b)
	{
		if (a.Bit >= 0)
		{
			return (BinaryPrimitives.ReadUInt16LittleEndian(b) & (1 << a.Bit)) != 0;
		}

		switch (a.FileType)
		{
			case SlcAddress.Float:
				float f = BinaryPrimitives.ReadSingleLittleEndian(b);

				return double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

			case SlcAddress.Long:
				return (long)BinaryPrimitives.ReadInt32LittleEndian(b);

			case SlcAddress.StringFile:
				int length = Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(b), 0, 82);

				return Text(b.Slice(2, 82), length);

			case SlcAddress.Ascii:
				return Text(b.Slice(0, 2), 2).TrimEnd('\0');

			default:
				return (long)BinaryPrimitives.ReadInt16LittleEndian(b);
		}
	}

	private string Text(ReadOnlySpan<byte> chars, int length)
	{
		var bytes = chars.ToArray();

		if (swapStrings)
		{
			for (int i = 0; i + 1 < bytes.Length; i += 2)
			{
				(bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
			}
		}

		return Encoding.Latin1.GetString(bytes, 0, Math.Min(length, bytes.Length));
	}

	private byte[] EncodeText(string s, int size)
	{
		var bytes = new byte[size];
		Encoding.Latin1.GetBytes(s.AsSpan(0, Math.Min(s.Length, size)), bytes);

		if (swapStrings)
		{
			for (int i = 0; i + 1 < bytes.Length; i += 2)
			{
				(bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
			}
		}

		return bytes;
	}

	public WriteOutcome[] Write(IReadOnlyList<WriteItem> items)
	{
		return items.Select(WriteOne).ToArray();
	}

	private WriteOutcome WriteOne(WriteItem item)
	{
		var a = (SlcAddress)item.Point.Address;

		try
		{
			if (a.Bit >= 0)
			{
				// Masked write: the processor changes the one bit, whatever the
				// others are doing meanwhile.
				ushort mask = (ushort)(1 << a.Bit);
				bool on = (bool)item.Raw!;

				Guard(() =>
				{
					Plc.WriteMasked(a.FileType, a.FileNumber, a.Element, a.Word, mask, on ? mask : (ushort)0);

					return true;
				});

				return WriteOutcome.Success;
			}

			byte[] data;

			switch (a.FileType)
			{
				case SlcAddress.Float:
					data = new byte[4];
					BinaryPrimitives.WriteSingleLittleEndian(data, (float)(double)item.Raw!);
					break;

				case SlcAddress.Long:
					data = new byte[4];
					BinaryPrimitives.WriteInt32LittleEndian(data, (int)(long)item.Raw!);
					break;

				case SlcAddress.StringFile:
					string s = (string)item.Raw!;
					data = new byte[84];
					BinaryPrimitives.WriteInt16LittleEndian(data, (short)Math.Min(s.Length, 82));
					EncodeText(s, 82).CopyTo(data, 2);
					break;

				case SlcAddress.Ascii:
					data = EncodeText((string)item.Raw!, 2);
					break;

				default:
					data = new byte[2];
					BinaryPrimitives.WriteInt16LittleEndian(data, (short)(long)item.Raw!);
					break;
			}

			Guard(() =>
			{
				Plc.WriteRaw(a.FileType, a.FileNumber, a.Element, a.Word, data);

				return true;
			});

			return WriteOutcome.Success;
		}
		catch (ResponseException e)
		{
			return WriteOutcome.Fail(e.Message);
		}
	}

	/// <summary>
	/// A refusal from the processor stays a ResponseException; any other
	/// failure of the library means the connection is gone.
	/// </summary>
	private static T Guard<T>(Func<T> f)
	{
		try
		{
			return f();
		}
		catch (ResponseException)
		{
			throw;
		}
		catch (CommunicationException e)
		{
			throw new CommsLostException(e.Message, e);
		}
	}

	public void Dispose()
	{
		try
		{
			plc?.Dispose();
		}
		catch (Exception)
		{
			// Closing a dead session.
		}

		plc = null;
	}
}
