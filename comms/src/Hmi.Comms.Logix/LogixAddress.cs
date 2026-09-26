using System.Text;
using System.Text.RegularExpressions;
using Hmi.Comms.Core;

namespace Hmi.Comms.Logix;

/// <summary>
/// A ControlLogix / CompactLogix tag: Tag, Program:Main.Tag, Udt.Member,
/// Arr[3], Arr[1,2], Tag.Member[2].Sub, and a bit of an integer, Dint.5.
///
/// Logix names are case-insensitive, so the key is upper-cased; the spelling
/// shown back is the user's. The value type is not declared -- the
/// controller reports it on the first read, and writes use what it reported.
/// </summary>
public sealed partial class LogixAddress : ParsedAddress
{
	// A name segment, optionally indexed with up to three dimensions.
	private const string Segment = @"[A-Za-z_][A-Za-z0-9_]{0,39}(?:\[\d{1,6}(?:,\d{1,6}){0,2}\])?";

	[GeneratedRegex(@"^(?:Program:[A-Za-z_][A-Za-z0-9_]{0,39}\.)?" + Segment + @"(?:\." + Segment + @")*(?:\.(\d{1,2}))?$",
		RegexOptions.CultureInvariant)]
	private static partial Regex Grammar();

	private LogixAddress(string text, string readTag, int bit)
	{
		Text = text;
		ReadTag = readTag;
		Bit = bit;
	}

	/// <summary>The tag as written (trimmed).</summary>
	public string Text { get; }

	/// <summary>What is actually read: the tag itself, or the integer a bit belongs to.</summary>
	public string ReadTag { get; }

	/// <summary>Case-insensitive identity of ReadTag; bits of one integer share it.</summary>
	public string ReadKey => ReadTag.ToUpperInvariant();

	/// <summary>Bit within an integer, or -1.</summary>
	public int Bit { get; }

	/// <summary>
	/// For a tag ending in a single index (Bits[37]): the array and the index.
	/// Needed because a BOOL array is packed in DWORDs, and element numbers on
	/// it address the DWORDs, not the bools.
	/// </summary>
	public (string Array, int Index)? TrailingIndex
	{
		get
		{
			if (Bit >= 0 || !ReadTag.EndsWith(']'))
			{
				return null;
			}

			int open = ReadTag.LastIndexOf('[');
			string inside = ReadTag.Substring(open + 1, ReadTag.Length - open - 2);

			return int.TryParse(inside, out int i) ? (ReadTag.Substring(0, open), i) : null;
		}
	}

	public override string Key => Text.ToUpperInvariant();

	public override string Normalized => Text;

	public override DataType Type => Bit >= 0 ? DataType.Bool : DataType.Unknown;

	/// <summary>Logix STRING holds 82 characters.</summary>
	public override int? MaxLength => 82;

	/// <summary>Estimated size of the IOI (request path) in bytes, for batch planning.</summary>
	public int PathBytes
	{
		get
		{
			int bytes = 0;

			foreach (var part in ReadTag.Split('.'))
			{
				string name = part;
				int bracket = name.IndexOf('[');

				if (bracket >= 0)
				{
					foreach (var idx in name.Substring(bracket + 1).TrimEnd(']').Split(','))
					{
						int i = int.Parse(idx);
						bytes += i < 256 ? 2 : i < 65536 ? 4 : 6;
					}

					name = name.Substring(0, bracket);
				}

				int n = Encoding.ASCII.GetByteCount(name);
				bytes += 2 + n + (n % 2);
			}

			return bytes;
		}
	}

	public static ParseResult Parse(string text)
	{
		string s = (text ?? "").Trim();

		if (s.Length == 0)
		{
			return ParseResult.Fail("Tag name is required");
		}

		var m = Grammar().Match(s);

		if (!m.Success)
		{
			return ParseResult.Fail("Not a Logix tag name (e.g. Tank_Level, Program:Main.Pump.Run, Arr[3], Status.5)");
		}

		if (m.Groups[1].Success)
		{
			int bit = int.Parse(m.Groups[1].Value);

			if (bit > 63)
			{
				return ParseResult.Fail("Bit must be 0 to 63");
			}

			string baseTag = s.Substring(0, s.Length - m.Groups[1].Value.Length - 1);

			return ParseResult.Ok(new LogixAddress(s, baseTag, bit));
		}

		return ParseResult.Ok(new LogixAddress(s, s, -1));
	}
}
