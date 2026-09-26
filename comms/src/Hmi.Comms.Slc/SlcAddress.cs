using System.Text.RegularExpressions;
using Hmi.Comms.Core;

namespace Hmi.Comms.Slc;

/// <summary>
/// An SLC 500 / MicroLogix data-table address:
///   N7:0  N7:0/3  B3:2/5  B3/37  F8:1  L9:0  L9:0/31  S:1/5  A10:0  ST9:0
///   T4:0.ACC|PRE|EN|TT|DN   C5:0.ACC|PRE|CU|CD|DN|OV|UN
///   R6:0.LEN|POS|EN|EU|DN|EM|ER|UL|IN|FD   I:1.0/3   O:2.0
///
/// Each resolves to a file (type and number), an element, a word within the
/// element and optionally a bit: which bytes of the data table it occupies.
/// </summary>
public sealed partial class SlcAddress : ParsedAddress
{
	public const byte Output = 0x82, Input = 0x83, StatusFile = 0x84, BitFile = 0x85, Timer = 0x86,
		Counter = 0x87, Control = 0x88, Integer = 0x89, Float = 0x8A, StringFile = 0x8D, Ascii = 0x8E,
		Long = 0x91;

	[GeneratedRegex(@"^(N|F|B|L|S|A)(\d{1,3})?:(\d{1,3})(?:/(\d{1,2}))?$", RegexOptions.IgnoreCase)]
	private static partial Regex DataFile();

	[GeneratedRegex(@"^(B|N)(\d{1,3})/(\d{1,4})$", RegexOptions.IgnoreCase)]
	private static partial Regex BitOffset();

	[GeneratedRegex(@"^ST(\d{1,3}):(\d{1,3})$", RegexOptions.IgnoreCase)]
	private static partial Regex StringAddress();

	[GeneratedRegex(@"^(T|C|R)(\d{1,3}):(\d{1,3})(?:\.([A-Z]{2,3}))?$", RegexOptions.IgnoreCase)]
	private static partial Regex Structured();

	[GeneratedRegex(@"^(I|O)(\d{1,3})?:(\d{1,3})(?:\.(\d{1,3}))?(?:/(\d{1,2}))?$", RegexOptions.IgnoreCase)]
	private static partial Regex InputOutput();

	private SlcAddress(string normalized, byte fileType, int fileNumber, int element, int word, int bit,
		DataType type, int elementBytes, bool writable)
	{
		this.normalized = normalized;
		FileType = fileType;
		FileNumber = fileNumber;
		Element = element;
		Word = word;
		Bit = bit;
		type_ = type;
		ElementBytes = elementBytes;
		writable_ = writable;
	}

	private readonly string normalized;
	private readonly DataType type_;
	private readonly bool writable_;

	public byte FileType { get; }
	public int FileNumber { get; }
	public int Element { get; }

	/// <summary>Word within the element (ACC is word 2 of a timer).</summary>
	public int Word { get; }

	/// <summary>Bit within the word, or -1.</summary>
	public int Bit { get; }

	/// <summary>Size of one element of the file.</summary>
	public int ElementBytes { get; }

	/// <summary>I/O is addressed by slot and cannot be read as a run with neighbours.</summary>
	public bool IsIO => FileType is Input or Output;

	/// <summary>Bytes this point needs, from the start of its word.</summary>
	public int ValueBytes => Bit >= 0 ? 2 : type_ switch
	{
		DataType.Float32 or DataType.Int32 => 4,
		DataType.String when FileType == StringFile => 84,
		_ => 2
	};

	public override string Key => normalized;
	public override string Normalized => normalized;
	public override DataType Type => type_;
	public override bool Writable => writable_;

	public override int? MaxLength => FileType switch
	{
		StringFile => 82,
		Ascii => 2,
		_ => null
	};

	public static int ElementSize(byte fileType) => fileType switch
	{
		Float or Long => 4,
		Timer or Counter or Control => 6,
		StringFile => 84,
		_ => 2
	};

	public static ParseResult Parse(string text)
	{
		string s = (text ?? "").Trim().ToUpperInvariant();
		Match m;

		if ((m = StringAddress().Match(s)).Success)
		{
			int file = int.Parse(m.Groups[1].Value), el = int.Parse(m.Groups[2].Value);

			return Ok($"ST{file}:{el}", StringFile, file, el, 0, -1, DataType.String);
		}

		if ((m = BitOffset().Match(s)).Success)
		{
			// B3/37 counts bits from the start of the file: word 2, bit 5.
			string letter = m.Groups[1].Value;
			int file = int.Parse(m.Groups[2].Value), n = int.Parse(m.Groups[3].Value);
			byte type = letter == "B" ? BitFile : Integer;

			return Ok($"{letter}{file}:{n / 16}/{n % 16}", type, file, n / 16, 0, n % 16, DataType.Bool);
		}

		if ((m = Structured().Match(s)).Success)
		{
			return ParseStructured(m);
		}

		if ((m = InputOutput().Match(s)).Success)
		{
			bool input = m.Groups[1].Value == "I";
			int file = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : (input ? 1 : 0);
			int slot = int.Parse(m.Groups[3].Value);
			int word = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
			int bit = m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : -1;

			if (bit > 15)
			{
				return ParseResult.Fail("Bit must be 0 to 15");
			}

			string name = (input ? "I:" : "O:") + slot + "." + word + (bit >= 0 ? "/" + bit : "");

			return ParseResult.Ok(new SlcAddress(name, input ? Input : Output, file, slot, word, bit,
				bit >= 0 ? DataType.Bool : DataType.Int16, 2, !input));
		}

		if ((m = DataFile().Match(s)).Success)
		{
			return ParseDataFile(m);
		}

		return ParseResult.Fail("Not an SLC address (e.g. N7:0, B3:1/4, F8:2, T4:0.ACC, ST9:0, I:1.0/3)");
	}

	private static ParseResult ParseDataFile(Match m)
	{
		string letter = m.Groups[1].Value;
		int? fileGiven = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
		int el = int.Parse(m.Groups[3].Value);
		int bit = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : -1;

		if (fileGiven == null && letter != "S")
		{
			return ParseResult.Fail($"{letter} needs a file number, e.g. {letter}{DefaultFile(letter)}:{el}");
		}

		int file = fileGiven ?? 2;

		var (type, dataType) = letter switch
		{
			"N" => (Integer, DataType.Int16),
			"F" => (Float, DataType.Float32),
			"B" => (BitFile, DataType.Int16),
			"L" => (Long, DataType.Int32),
			"S" => (StatusFile, DataType.Int16),
			_ => (Ascii, DataType.String)
		};

		if (bit >= 0)
		{
			int max = type == Long ? 31 : 15;

			if (type is Float or Ascii)
			{
				return ParseResult.Fail($"{letter} elements have no bits");
			}

			if (bit > max)
			{
				return ParseResult.Fail($"Bit must be 0 to {max}");
			}

			// A bit of a LONG above 15 lives in its second word.
			int word = bit / 16;

			return Ok($"{(letter == "S" && file == 2 ? "S" : letter + file)}:{el}/{bit}", type, file, el,
				word, bit % 16, DataType.Bool);
		}

		// The status file is file 2; S:1 and S2:1 are one word.
		string name = letter == "S" && file == 2 ? $"S:{el}" : $"{letter}{file}:{el}";

		return Ok(name, type, file, el, 0, -1, dataType);
	}

	private static ParseResult ParseStructured(Match m)
	{
		string letter = m.Groups[1].Value;
		int file = int.Parse(m.Groups[2].Value), el = int.Parse(m.Groups[3].Value);
		string? member = m.Groups[4].Success ? m.Groups[4].Value : null;
		byte type = letter switch { "T" => Timer, "C" => Counter, _ => Control };

		if (member == null)
		{
			string examples = letter switch
			{
				"T" => "ACC, PRE, EN, TT or DN",
				"C" => "ACC, PRE, CU, CD, DN, OV or UN",
				_ => "LEN, POS, EN, EU, DN, EM, ER, UL, IN or FD"
			};

			return ParseResult.Fail($"Name a member of {letter}{file}:{el}: {examples}");
		}

		// Words: 0 holds the status bits, then PRE and ACC (timers, counters)
		// or LEN and POS (control).
		(int word, int bit)? at = (letter, member) switch
		{
			("T" or "C", "PRE") => (1, -1),
			("T" or "C", "ACC") => (2, -1),
			("T", "EN") => (0, 15),
			("T", "TT") => (0, 14),
			("T", "DN") => (0, 13),
			("C", "CU") => (0, 15),
			("C", "CD") => (0, 14),
			("C", "DN") => (0, 13),
			("C", "OV") => (0, 12),
			("C", "UN") => (0, 11),
			("C", "UA") => (0, 10),
			("R", "LEN") => (1, -1),
			("R", "POS") => (2, -1),
			("R", "EN") => (0, 15),
			("R", "EU") => (0, 14),
			("R", "DN") => (0, 13),
			("R", "EM") => (0, 12),
			("R", "ER") => (0, 11),
			("R", "UL") => (0, 10),
			("R", "IN") => (0, 9),
			("R", "FD") => (0, 8),
			_ => null
		};

		if (at == null)
		{
			return ParseResult.Fail($"{letter} has no member {member}");
		}

		var (w, b) = at.Value;

		return Ok($"{letter}{file}:{el}.{member}", type, file, el, w, b, b >= 0 ? DataType.Bool : DataType.Int16);
	}

	private static int DefaultFile(string letter) => letter switch
	{
		"B" => 3,
		"N" => 7,
		"F" => 8,
		_ => 9
	};

	private static ParseResult Ok(string name, byte type, int file, int el, int word, int bit, DataType dt)
	{
		return ParseResult.Ok(new SlcAddress(name, type, file, el, word, bit, dt, ElementSize(type), true));
	}
}
