using System.Globalization;

namespace Hmi.Comms.Core;

/// <summary>
/// Conversions between client values and point types.
///
/// Values inside the server are one of: bool, long, ulong, double, string or
/// null. Drivers produce those; clients send JSON numbers, booleans and
/// strings, which are coerced here to the exact type of the point before a
/// write ever reaches a device.
/// </summary>
public static class ValueCodec
{
	/// <summary>Numeric view of a value, when it has one.</summary>
	public static bool TryToDouble(object? value, out double d)
	{
		switch (value)
		{
			case double x:
				d = x;
				return true;
			case float f:
				d = f;
				return true;
			case long l:
				d = l;
				return true;
			case ulong u:
				d = u;
				return true;
			case int i:
				d = i;
				return true;
			case bool b:
				d = b ? 1 : 0;
				return true;
			case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p):
				d = p;
				return true;
			default:
				d = 0;
				return false;
		}
	}

	/// <summary>
	/// Coerces a client value to a point's type. Integers are rounded and
	/// range-checked; strings must fit. Unknown types pass the value through
	/// for the driver to resolve.
	/// </summary>
	public static bool TryCoerce(object? value, DataType type, int? maxLength, out object? raw, out string? error)
	{
		raw = null;
		error = null;

		if (value == null)
		{
			error = "No value";

			return false;
		}

		switch (type)
		{
			case DataType.Unknown:
				raw = value is int i ? (long)i : value;

				return true;

			case DataType.Bool:
				if (value is bool b)
				{
					raw = b;

					return true;
				}

				if (value is string s)
				{
					switch (s.Trim().ToLowerInvariant())
					{
						case "true":
						case "on":
						case "1":
							raw = true;
							return true;
						case "false":
						case "off":
						case "0":
							raw = false;
							return true;
					}
				}

				if (TryToDouble(value, out double bd) && !double.IsNaN(bd))
				{
					raw = bd != 0;

					return true;
				}

				error = "Not a boolean";

				return false;

			case DataType.String:
				string text = value switch
				{
					string str => str,
					bool bb => bb ? "1" : "0",
					double dd => dd.ToString("R", CultureInfo.InvariantCulture),
					_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
				};

				if (maxLength != null && text.Length > maxLength)
				{
					error = $"Longer than {maxLength} characters";

					return false;
				}

				raw = text;

				return true;
		}

		if (!TryToDouble(value, out double d))
		{
			error = "Not a number";

			return false;
		}

		if (double.IsNaN(d) || double.IsInfinity(d))
		{
			error = "Not a finite number";

			return false;
		}

		if (DataTypes.IsFloat(type))
		{
			if (type == DataType.Float32 && Math.Abs(d) > float.MaxValue)
			{
				error = "Out of range for REAL";

				return false;
			}

			raw = d;

			return true;
		}

		double r = Math.Round(d, MidpointRounding.AwayFromZero);
		var (min, max) = Range(type);

		if (r < min || r > max)
		{
			error = $"Out of range ({min} to {max})";

			return false;
		}

		raw = type == DataType.UInt64 ? (object)(ulong)r : (long)r;

		return true;
	}

	public static (double Min, double Max) Range(DataType t) => t switch
	{
		DataType.Int8 => (sbyte.MinValue, sbyte.MaxValue),
		DataType.UInt8 => (byte.MinValue, byte.MaxValue),
		DataType.Int16 => (short.MinValue, short.MaxValue),
		DataType.UInt16 => (ushort.MinValue, ushort.MaxValue),
		DataType.Int32 => (int.MinValue, int.MaxValue),
		DataType.UInt32 => (uint.MinValue, uint.MaxValue),
		DataType.Int64 => (long.MinValue, long.MaxValue),
		DataType.UInt64 => (ulong.MinValue, ulong.MaxValue),
		_ => (double.MinValue, double.MaxValue)
	};

	/// <summary>
	/// Raw to what the client sees: scaled when the tag scales and the value is
	/// numeric, otherwise unchanged.
	/// </summary>
	public static object? ToClient(object? raw, Scaling? scale)
	{
		if (scale == null || raw is bool or string or null)
		{
			return raw;
		}

		return TryToDouble(raw, out double d) ? scale.ToEu(d) : raw;
	}

	/// <summary>Client value to raw, before coercion: the inverse scaling.</summary>
	public static object? FromClient(object? value, Scaling? scale)
	{
		// A numeric string ("12.5") scales like the number it spells.
		if (scale == null || value is bool or null)
		{
			return value;
		}

		return TryToDouble(value, out double d) ? scale.ToRaw(d) : value;
	}
}
