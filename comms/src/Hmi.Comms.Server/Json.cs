using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hmi.Comms.Server;

/// <summary>
/// JSON helpers over JsonNode and Utf8JsonWriter. No reflection-based
/// serialization anywhere, so the published single-file executable can be
/// trimmed safely.
/// </summary>
internal static class Json
{
	public static byte[] Build(Action<Utf8JsonWriter> body)
	{
		var buffer = new ArrayBufferWriter<byte>(256);

		using (var w = new Utf8JsonWriter(buffer))
		{
			w.WriteStartObject();
			body(w);
			w.WriteEndObject();
		}

		return buffer.WrittenSpan.ToArray();
	}

	public static string? Str(JsonObject o, string key)
	{
		return o.TryGetPropertyValue(key, out var n) && n is JsonValue v &&
			v.TryGetValue<string>(out var s) ? s : null;
	}

	public static long? Long(JsonObject o, string key)
	{
		if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v)
		{
			return null;
		}

		if (v.TryGetValue<long>(out var l))
		{
			return l;
		}

		if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) &&
			d >= long.MinValue && d <= long.MaxValue)
		{
			return (long)d;
		}

		return null;
	}

	public static double? Double(JsonObject o, string key)
	{
		return o.TryGetPropertyValue(key, out var n) && n is JsonValue v &&
			v.TryGetValue<double>(out var d) ? d : null;
	}

	public static bool? Bool(JsonObject o, string key)
	{
		return o.TryGetPropertyValue(key, out var n) && n is JsonValue v &&
			v.TryGetValue<bool>(out var b) ? b : null;
	}

	public static JsonArray? Array(JsonObject o, string key)
	{
		return o.TryGetPropertyValue(key, out var n) ? n as JsonArray : null;
	}

	public static JsonObject? Object(JsonObject o, string key)
	{
		return o.TryGetPropertyValue(key, out var n) ? n as JsonObject : null;
	}
}
