using System.Text.RegularExpressions;
using Hmi.Comms.Core;

namespace Hmi.Comms.Modbus;

public enum ModbusTable
{
	HR,
	IR,
	CO,
	DI
}

public enum ModbusType
{
	BOOL,
	INT16,
	UINT16,
	INT32,
	UINT32,
	FLOAT,
	INT64,
	UINT64,
	DOUBLE,
	STRING
}

/// <summary>
/// Byte order of multi-byte values, named as AppendProbe names them, for the
/// wire bytes A B C D of a 32-bit value:
///   BE  ABCD  big-endian (Modbus standard)
///   LE  DCBA  fully reversed
///   MBE BADC  bytes swapped within each 16-bit word
///   MLE CDAB  word order reversed ("word swap", the common PLC float order)
/// The same rule extends to 16 bits (MBE and LE swap the two bytes; MLE leaves
/// them) and to 64 bits (MLE reverses all four words).
/// </summary>
public enum ByteOrder
{
	BE,
	LE,
	MBE,
	MLE
}

/// <summary>
/// A Modbus address: HR|IR|CO|DI:&lt;offset&gt;[.bit][:TYPE][:ORDER].
///
/// Offsets are the 0-based protocol addresses. Classic 5- and 6-digit
/// references (40001, 300001) are refused with a pointer to the right form:
/// accepting both styles invites off-by-one mistakes nobody notices until a
/// pump starts.
/// </summary>
public sealed partial class ModbusAddress : ParsedAddress
{
	[GeneratedRegex(@"^(HR|IR|CO|DI):(\d{1,5})(?:\.(\d{1,2}))?(?::([A-Z]+[0-9]*))?(?::([A-Z]+))?$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Grammar();

	[GeneratedRegex(@"^([0134])(\d{4,5})$")]
	private static partial Regex ClassicReference();

	public ModbusAddress(byte unit, ModbusTable table, int offset, int bit, ModbusType type,
		int stringLength, ByteOrder order)
	{
		Unit = unit;
		Table = table;
		Offset = offset;
		Bit = bit;
		MType = type;
		StringLength = stringLength;
		Order = order;
	}

	public byte Unit { get; }
	public ModbusTable Table { get; }
	public int Offset { get; }

	/// <summary>Bit number within a register, or -1.</summary>
	public int Bit { get; }

	public ModbusType MType { get; }
	public int StringLength { get; }
	public ByteOrder Order { get; }

	public bool IsBitTable => Table is ModbusTable.CO or ModbusTable.DI;

	/// <summary>Registers (or coils) occupied.</summary>
	public int Count => IsBitTable || Bit >= 0 ? 1 : MType switch
	{
		ModbusType.INT16 or ModbusType.UINT16 => 1,
		ModbusType.INT32 or ModbusType.UINT32 or ModbusType.FLOAT => 2,
		ModbusType.INT64 or ModbusType.UINT64 or ModbusType.DOUBLE => 4,
		ModbusType.STRING => (StringLength + 1) / 2,
		_ => 1
	};

	public int End => Offset + Count - 1;

	public override string Normalized
	{
		get
		{
			string t = Table.ToString();

			if (IsBitTable)
			{
				return $"{t}:{Offset}";
			}

			if (Bit >= 0)
			{
				return $"{t}:{Offset}.{Bit}";
			}

			string type = MType == ModbusType.STRING ? "STRING" + StringLength : MType.ToString();

			return $"{t}:{Offset}:{type}:{Order}";
		}
	}

	public override string Key => Unit + "/" + Normalized;

	public override DataType Type => IsBitTable || Bit >= 0 ? DataType.Bool : MType switch
	{
		ModbusType.INT16 => DataType.Int16,
		ModbusType.UINT16 => DataType.UInt16,
		ModbusType.INT32 => DataType.Int32,
		ModbusType.UINT32 => DataType.UInt32,
		ModbusType.FLOAT => DataType.Float32,
		ModbusType.INT64 => DataType.Int64,
		ModbusType.UINT64 => DataType.UInt64,
		ModbusType.DOUBLE => DataType.Float64,
		ModbusType.STRING => DataType.String,
		_ => DataType.Bool
	};

	public override bool Writable => Table is ModbusTable.HR or ModbusTable.CO;

	public override int? MaxLength => MType == ModbusType.STRING ? StringLength : null;

	public static ParseResult Parse(string text, byte unit, ByteOrder defaultOrder, string? hint)
	{
		string s = (text ?? "").Trim();

		var classic = ClassicReference().Match(s);

		if (classic.Success)
		{
			return ParseResult.Fail(ClassicHint(classic.Groups[1].Value, classic.Groups[2].Value));
		}

		var m = Grammar().Match(s);

		if (!m.Success)
		{
			return ParseResult.Fail("Expected HR|IR|CO|DI:<offset>[.bit][:TYPE][:ORDER], e.g. HR:100:FLOAT");
		}

		var table = Enum.Parse<ModbusTable>(m.Groups[1].Value, ignoreCase: true);
		int offset = int.Parse(m.Groups[2].Value);
		int bit = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : -1;
		string? typeText = m.Groups[4].Success ? m.Groups[4].Value.ToUpperInvariant() : null;
		string? orderText = m.Groups[5].Success ? m.Groups[5].Value.ToUpperInvariant() : null;

		if (offset > 65535)
		{
			return ParseResult.Fail("Offset must be 0 to 65535");
		}

		// A lone order, as in HR:100:MLE.
		if (typeText != null && orderText == null && Enum.TryParse<ByteOrder>(typeText, out _))
		{
			orderText = typeText;
			typeText = null;
		}

		var order = defaultOrder;

		if (orderText != null && !Enum.TryParse(orderText, out order))
		{
			return ParseResult.Fail($"Unknown byte order '{orderText}' (BE, LE, MBE or MLE)");
		}

		bool bitTable = table is ModbusTable.CO or ModbusTable.DI;

		if (bitTable)
		{
			if (bit >= 0)
			{
				return ParseResult.Fail($"{table} is already a bit; no .bit suffix");
			}

			if (typeText != null && typeText != "BOOL")
			{
				return ParseResult.Fail($"{table} holds bits; only BOOL applies");
			}

			return ParseResult.Ok(new ModbusAddress(unit, table, offset, -1, ModbusType.BOOL, 0, order));
		}

		if (bit >= 0)
		{
			if (bit > 15)
			{
				return ParseResult.Fail("Bit must be 0 to 15");
			}

			if (typeText != null && typeText != "BOOL")
			{
				return ParseResult.Fail("A bit of a register is BOOL");
			}

			return ParseResult.Ok(new ModbusAddress(unit, table, offset, bit, ModbusType.BOOL, 0, order));
		}

		ModbusType type;
		int length = 0;

		if (typeText == null)
		{
			type = FromHint(hint, out string? hintError);

			if (hintError != null)
			{
				return ParseResult.Fail(hintError);
			}
		}
		else if (typeText.StartsWith("STRING"))
		{
			if (typeText.Length == 6 || !int.TryParse(typeText.AsSpan(6), out length) ||
				length < 1 || length > 250)
			{
				return ParseResult.Fail("STRING needs a length of 1 to 250 characters, e.g. STRING20");
			}

			type = ModbusType.STRING;
		}
		else if (typeText == "BOOL")
		{
			return ParseResult.Fail($"A register is not a bit; use {table}:{offset}.0 for bit 0");
		}
		else if (!Enum.TryParse(typeText, out type))
		{
			return ParseResult.Fail($"Unknown type '{typeText}' (INT16, UINT16, INT32, UINT32, " +
				"FLOAT, INT64, UINT64, DOUBLE, STRINGn)");
		}

		var a = new ModbusAddress(unit, table, offset, -1, type, length, order);

		if (a.End > 65535)
		{
			return ParseResult.Fail($"{type} at {offset} runs past register 65535");
		}

		return ParseResult.Ok(a);
	}

	/// <summary>A type from the client's hint when the address names none. Integer by default.</summary>
	private static ModbusType FromHint(string? hint, out string? error)
	{
		error = null;

		return DataTypes.ParseHint(hint) switch
		{
			DataType.UInt16 => ModbusType.UINT16,
			DataType.Int32 => ModbusType.INT32,
			DataType.UInt32 => ModbusType.UINT32,
			DataType.Float32 => ModbusType.FLOAT,
			DataType.Int64 => ModbusType.INT64,
			DataType.UInt64 => ModbusType.UINT64,
			DataType.Float64 => ModbusType.DOUBLE,
			DataType.String => Fail("A string register needs a length, e.g. HR:100:STRING20", out error),
			_ => ModbusType.INT16
		};
	}

	private static ModbusType Fail(string message, out string error)
	{
		error = message;

		return ModbusType.INT16;
	}

	private static string ClassicHint(string prefix, string digits)
	{
		int n = int.Parse(digits);
		string table = prefix switch
		{
			"4" => "HR",
			"3" => "IR",
			"1" => "DI",
			_ => "CO"
		};

		return n >= 1 ?
			$"Use {table}:{n - 1} for {prefix}{digits} (offsets are 0-based: table and offset)" :
			$"Use {table}:<offset> (offsets are 0-based)";
	}
}
