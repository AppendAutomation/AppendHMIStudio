using System.Buffers.Binary;
using System.Text;

namespace Hmi.Comms.Modbus;

/// <summary>
/// Register bytes to values and back.
///
/// Register data arrives as wire bytes, two per register, high byte first.
/// Each byte order is a permutation of those bytes into plain big-endian --
/// and every one of them is its own inverse, so the same permutation encodes.
/// </summary>
public static class ModbusCodec
{
	/// <summary>Reorders wire bytes to big-endian (or big-endian back to wire bytes).</summary>
	public static void Reorder(ReadOnlySpan<byte> src, Span<byte> dst, ByteOrder order)
	{
		int n = src.Length;

		switch (order)
		{
			case ByteOrder.BE:
				src.CopyTo(dst);
				break;

			case ByteOrder.LE:
				for (int i = 0; i < n; i++)
				{
					dst[i] = src[n - 1 - i];
				}

				break;

			case ByteOrder.MBE:
				for (int i = 0; i + 1 < n; i += 2)
				{
					dst[i] = src[i + 1];
					dst[i + 1] = src[i];
				}

				break;

			case ByteOrder.MLE:
				int words = n / 2;

				for (int w = 0; w < words; w++)
				{
					dst[2 * w] = src[2 * (words - 1 - w)];
					dst[2 * w + 1] = src[2 * (words - 1 - w) + 1];
				}

				break;
		}
	}

	/// <summary>Decodes a value from its registers' wire bytes.</summary>
	public static object? Decode(ModbusAddress a, ReadOnlySpan<byte> wire)
	{
		if (a.Bit >= 0)
		{
			Span<byte> w = stackalloc byte[2];
			Reorder(wire.Slice(0, 2), w, a.Order);
			ushort reg = BinaryPrimitives.ReadUInt16BigEndian(w);

			return (reg & (1 << a.Bit)) != 0;
		}

		if (a.MType == ModbusType.STRING)
		{
			return DecodeString(wire.Slice(0, a.Count * 2), a.StringLength, a.Order);
		}

		int size = a.Count * 2;
		Span<byte> be = stackalloc byte[size];
		Reorder(wire.Slice(0, size), be, a.Order);

		return a.MType switch
		{
			ModbusType.INT16 => (long)BinaryPrimitives.ReadInt16BigEndian(be),
			ModbusType.UINT16 => (long)BinaryPrimitives.ReadUInt16BigEndian(be),
			ModbusType.INT32 => (long)BinaryPrimitives.ReadInt32BigEndian(be),
			ModbusType.UINT32 => (long)BinaryPrimitives.ReadUInt32BigEndian(be),
			ModbusType.FLOAT => (double)BinaryPrimitives.ReadSingleBigEndian(be),
			ModbusType.INT64 => BinaryPrimitives.ReadInt64BigEndian(be),
			ModbusType.UINT64 => BinaryPrimitives.ReadUInt64BigEndian(be),
			ModbusType.DOUBLE => BinaryPrimitives.ReadDoubleBigEndian(be),
			_ => null
		};
	}

	/// <summary>Encodes a coerced value (long, ulong, double or string) into wire bytes.</summary>
	public static byte[] Encode(ModbusAddress a, object? raw)
	{
		if (a.MType == ModbusType.STRING)
		{
			return EncodeString((string)(raw ?? ""), a.Count, a.Order);
		}

		int size = a.Count * 2;
		var be = new byte[size];

		switch (a.MType)
		{
			case ModbusType.INT16:
				BinaryPrimitives.WriteInt16BigEndian(be, checked((short)ToLong(raw)));
				break;
			case ModbusType.UINT16:
				BinaryPrimitives.WriteUInt16BigEndian(be, checked((ushort)ToLong(raw)));
				break;
			case ModbusType.INT32:
				BinaryPrimitives.WriteInt32BigEndian(be, checked((int)ToLong(raw)));
				break;
			case ModbusType.UINT32:
				BinaryPrimitives.WriteUInt32BigEndian(be, checked((uint)ToLong(raw)));
				break;
			case ModbusType.FLOAT:
				BinaryPrimitives.WriteSingleBigEndian(be, (float)Convert.ToDouble(raw));
				break;
			case ModbusType.INT64:
				BinaryPrimitives.WriteInt64BigEndian(be, ToLong(raw));
				break;
			case ModbusType.UINT64:
				BinaryPrimitives.WriteUInt64BigEndian(be, raw is ulong u ? u : checked((ulong)ToLong(raw)));
				break;
			case ModbusType.DOUBLE:
				BinaryPrimitives.WriteDoubleBigEndian(be, Convert.ToDouble(raw));
				break;
			default:
				throw new ArgumentException($"Cannot encode {a.MType}");
		}

		var wire = new byte[size];
		Reorder(be, wire, a.Order);

		return wire;
	}

	private static long ToLong(object? raw) => raw switch
	{
		long l => l,
		ulong u => checked((long)u),
		double d => (long)Math.Round(d),
		bool b => b ? 1 : 0,
		_ => Convert.ToInt64(raw)
	};

	/// <summary>
	/// Characters are packed two per register, first character in the high
	/// byte for BE and MLE, in the low byte for LE and MBE (which swap bytes).
	/// </summary>
	public static string DecodeString(ReadOnlySpan<byte> wire, int length, ByteOrder order)
	{
		var bytes = new byte[wire.Length];
		bool swap = order is ByteOrder.LE or ByteOrder.MBE;

		for (int i = 0; i + 1 < wire.Length; i += 2)
		{
			bytes[i] = swap ? wire[i + 1] : wire[i];
			bytes[i + 1] = swap ? wire[i] : wire[i + 1];
		}

		int n = Math.Min(length, bytes.Length);
		int end = Array.IndexOf(bytes, (byte)0, 0, n);

		return Encoding.Latin1.GetString(bytes, 0, end >= 0 ? end : n);
	}

	public static byte[] EncodeString(string text, int registers, ByteOrder order)
	{
		var bytes = new byte[registers * 2];
		Encoding.Latin1.GetBytes(text.AsSpan(0, Math.Min(text.Length, bytes.Length)), bytes);

		if (order is ByteOrder.LE or ByteOrder.MBE)
		{
			for (int i = 0; i + 1 < bytes.Length; i += 2)
			{
				(bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
			}
		}

		return bytes;
	}

	/// <summary>Bit i of coil data, packed least significant bit first.</summary>
	public static bool CoilBit(ReadOnlySpan<byte> packed, int i)
	{
		return (packed[i / 8] & (1 << (i % 8))) != 0;
	}
}
