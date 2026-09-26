using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hmi.Comms.Core;

namespace Hmi.Comms.Server;

/// <summary>
/// The protocol's data messages for one session, over an EngineSession.
///
/// Values travel as compact tuples, [handle, value, quality, timestamp] plus
/// status and error when bad, keyed by the integer handles configure hands
/// out; requests may name tags by id or handle.
/// </summary>
internal sealed class DataSession : ISessionHandler
{
	private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(5);
	private const double MaxSafeInteger = 9007199254740991;

	private readonly Session session;
	private readonly EngineSession engine;
	private readonly CommsEngine commsEngine;
	private readonly object gate = new();

	public DataSession(Session session, CommsEngine commsEngine)
	{
		this.session = session;
		this.commsEngine = commsEngine;
		engine = commsEngine.CreateSession();
		engine.ChangesAvailable = session.SignalChanges;
		engine.StatusChanged = PushStatus;
		session.PullChanges = PullChanges;
	}

	public async Task<bool> HandleAsync(string type, long? id, JsonObject msg)
	{
		switch (type)
		{
			case "configure":
				Configure(id, msg);
				return true;
			case "subscribe":
				Subscribe(id, msg);
				return true;
			case "unsubscribe":
				engine.Unsubscribe();
				session.Reply(id, "unsubscribeResult", w => w.WriteBoolean("ok", true));
				return true;
			case "read":
				await ReadAsync(id, msg);
				return true;
			case "write":
				await WriteAsync(id, msg);
				return true;
			case "validate":
				Validate(id, msg);
				return true;
			case "status":
				session.Reply(id, "statusResult", w => WriteStatuses(w, engine.Statuses()));
				return true;
			case "diag":
				Diag(id);
				return true;
			default:
				return false;
		}
	}

	// ------------------------------------------------------------ configure

	private void Configure(long? id, JsonObject msg)
	{
		var devices = new List<DeviceConfig>();
		var tags = new List<TagConfig>();

		foreach (var node in Json.Array(msg, "devices") ?? new JsonArray())
		{
			if (node is not JsonObject d)
			{
				throw new ProtocolException("bad_request", "Each device must be an object");
			}

			devices.Add(ParseDevice(d));
		}

		foreach (var node in Json.Array(msg, "tags") ?? new JsonArray())
		{
			if (node is not JsonObject t)
			{
				throw new ProtocolException("bad_request", "Each tag must be an object");
			}

			tags.Add(ParseTag(t));
		}

		var result = engine.Configure(devices, tags);

		session.Reply(id, "configureResult", w =>
		{
			w.WriteBoolean("ok", true);
			w.WriteStartArray("tags");

			foreach (var t in result)
			{
				w.WriteStartObject();
				w.WriteString("id", t.Id);
				w.WriteNumber("h", t.Handle);

				if (t.Normalized != null)
				{
					w.WriteString("normalized", t.Normalized);
				}

				if (t.Error != null)
				{
					w.WriteString("error", t.Error);
				}

				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteStartArray("errors");

			foreach (var t in result.Where(t => t.Error != null))
			{
				w.WriteStartObject();
				w.WriteString("id", t.Id);
				w.WriteString("error", t.Error);
				w.WriteEndObject();
			}

			w.WriteEndArray();
		});

		Log.Info($"session {session.Id} configured {devices.Count} devices, {tags.Count} tags " +
			$"({result.Count(t => t.Error != null)} with errors)");
	}

	internal static DeviceConfig ParseDevice(JsonObject d)
	{
		string name = Json.Str(d, "name") ?? throw new ProtocolException("bad_request", "Device needs a name");
		string protocol = Json.Str(d, "protocol") ??
			throw new ProtocolException("bad_request", $"Device '{name}' needs a protocol");

		var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (Json.Object(d, "options") is JsonObject o)
		{
			foreach (var kv in o)
			{
				if (kv.Value is JsonValue v)
				{
					options[kv.Key] = ScalarToString(v);
				}
			}
		}

		return new DeviceConfig
		{
			Name = name,
			Protocol = protocol,
			Host = Json.Str(d, "host") ?? "",
			Port = (int?)Json.Long(d, "port"),
			TimeoutMs = (int)Math.Clamp(Json.Long(d, "timeoutMs") ?? 3000, 100, 60000),
			MinScanMs = (int)Math.Clamp(Json.Long(d, "minScanMs") ?? 50, 10, 60000),
			Enabled = Json.Bool(d, "enabled") ?? true,
			Options = options
		};
	}

	internal static TagConfig ParseTag(JsonObject t)
	{
		string id = Json.Str(t, "id") ?? throw new ProtocolException("bad_request", "Tag needs an id");
		Scaling? scale = null;

		if (Json.Object(t, "scale") is JsonObject s)
		{
			double? rawMin = Json.Double(s, "rawMin"), rawMax = Json.Double(s, "rawMax");
			double? euMin = Json.Double(s, "euMin"), euMax = Json.Double(s, "euMax");

			if (rawMin == null || rawMax == null || euMin == null || euMax == null)
			{
				throw new ProtocolException("bad_request", $"Tag '{id}': scale needs rawMin, rawMax, euMin and euMax");
			}

			scale = new Scaling(rawMin.Value, rawMax.Value, euMin.Value, euMax.Value, Json.Bool(s, "clamp") ?? false);

			if (scale.IsIdentity)
			{
				scale = null;
			}
		}

		return new TagConfig
		{
			Id = id,
			Device = Json.Str(t, "device") ?? "",
			Address = Json.Str(t, "address") ?? "",
			DataType = Json.Str(t, "dataType"),
			Deadband = Math.Max(0, Json.Double(t, "deadband") ?? 0),
			Scale = scale,
			ReadOnly = Json.Bool(t, "readOnly") ?? false
		};
	}

	private static string ScalarToString(JsonValue v)
	{
		if (v.TryGetValue<string>(out var s))
		{
			return s;
		}

		if (v.TryGetValue<bool>(out var b))
		{
			return b ? "true" : "false";
		}

		if (v.TryGetValue<double>(out var d))
		{
			return d.ToString(CultureInfo.InvariantCulture);
		}

		return v.ToJsonString();
	}

	// ------------------------------------------------------------ subscribe

	private void Subscribe(long? id, JsonObject msg)
	{
		var (handles, unknown) = ResolveTags(Json.Array(msg, "tags"));
		int rate = (int)(Json.Long(msg, "rateMs") ?? 1000);

		// The reply and the snapshot are queued together, under the same lock
		// that draining changes takes, so the client sees the snapshot for its
		// new subscription before any change to it.
		lock (gate)
		{
			var snapshot = engine.Subscribe(handles, rate);

			session.Reply(id, "subscribeResult", w =>
			{
				w.WriteBoolean("ok", true);
				WriteUnknown(w, unknown);
			});

			session.Push(Json.Build(w =>
			{
				w.WriteString("t", "snapshot");

				if (id != null)
				{
					w.WriteNumber("sub", id.Value);
				}

				WriteValues(w, "values", snapshot);
			}));
		}
	}

	private byte[]? PullChanges()
	{
		lock (gate)
		{
			var changes = engine.DrainChanges();

			if (changes.Count == 0)
			{
				return null;
			}

			return Json.Build(w =>
			{
				w.WriteString("t", "change");
				WriteValues(w, "values", changes);
			});
		}
	}

	/// <summary>Tags named by handle (number) or id (string).</summary>
	private (List<int> Handles, List<string> Unknown) ResolveTags(JsonArray? list)
	{
		var handles = new List<int>();
		var unknown = new List<string>();

		foreach (var node in list ?? new JsonArray())
		{
			if (node is not JsonValue v)
			{
				continue;
			}

			if (v.TryGetValue<string>(out var sid))
			{
				if (engine.HandleOf(sid) is int h)
				{
					handles.Add(h);
				}
				else
				{
					unknown.Add(sid);
				}
			}
			else if (v.TryGetValue<double>(out var d) && engine.IdOf((int)d) != null)
			{
				handles.Add((int)d);
			}
			else
			{
				unknown.Add(v.ToJsonString());
			}
		}

		return (handles, unknown);
	}

	private static void WriteUnknown(Utf8JsonWriter w, List<string> unknown)
	{
		if (unknown.Count > 0)
		{
			w.WriteStartArray("unknown");

			foreach (var u in unknown)
			{
				w.WriteStringValue(u);
			}

			w.WriteEndArray();
		}
	}

	// ------------------------------------------------------------ read / write

	private async Task ReadAsync(long? id, JsonObject msg)
	{
		var (handles, unknown) = ResolveTags(Json.Array(msg, "tags"));
		var values = await engine.ReadAsync(handles, TimeSpan.FromSeconds(10));

		session.Reply(id, "readResult", w =>
		{
			WriteValues(w, "values", values);
			WriteUnknown(w, unknown);
		});
	}

	/// <summary>
	/// "values": {"TagId": value, ...} names tags by id; "handles": [[h, value], ...]
	/// by handle. Results come back keyed the same way.
	/// </summary>
	private async Task WriteAsync(long? id, JsonObject msg)
	{
		var requests = new List<(int Handle, object? Value)>();
		var keyOf = new Dictionary<int, string>();
		var results = new Dictionary<string, WriteOutcome>(StringComparer.Ordinal);

		if (Json.Object(msg, "values") is JsonObject byId)
		{
			foreach (var kv in byId)
			{
				if (engine.HandleOf(kv.Key) is int h)
				{
					requests.Add((h, FromJson(kv.Value)));
					keyOf[h] = kv.Key;
				}
				else
				{
					results[kv.Key] = WriteOutcome.Fail("Unknown tag");
				}
			}
		}

		if (Json.Array(msg, "handles") is JsonArray byHandle)
		{
			foreach (var node in byHandle)
			{
				if (node is JsonArray pair && pair.Count == 2 && pair[0] is JsonValue hv &&
					hv.TryGetValue<double>(out var hd))
				{
					requests.Add(((int)hd, FromJson(pair[1])));
					keyOf[(int)hd] = ((int)hd).ToString(CultureInfo.InvariantCulture);
				}
			}
		}

		var timeout = TimeSpan.FromMilliseconds(Math.Clamp(Json.Long(msg, "timeoutMs") ??
			(long)DefaultWriteTimeout.TotalMilliseconds, 100, 60000));

		foreach (var (h, outcome) in await engine.WriteAsync(requests, timeout))
		{
			results[keyOf.TryGetValue(h, out var k) ? k : h.ToString(CultureInfo.InvariantCulture)] = outcome;
		}

		session.Reply(id, "writeResult", w =>
		{
			w.WriteStartObject("results");

			foreach (var (key, outcome) in results)
			{
				w.WriteStartObject(key);
				w.WriteBoolean("ok", outcome.Ok);

				if (outcome.Error != null)
				{
					w.WriteString("error", outcome.Error);
				}

				w.WriteEndObject();
			}

			w.WriteEndObject();
		});
	}

	private static object? FromJson(JsonNode? node)
	{
		if (node is not JsonValue v)
		{
			return null;
		}

		if (v.TryGetValue<bool>(out var b))
		{
			return b;
		}

		if (v.TryGetValue<string>(out var s))
		{
			return s;
		}

		if (v.TryGetValue<long>(out var l))
		{
			return l;
		}

		return v.TryGetValue<double>(out var d) ? d : null;
	}

	// ------------------------------------------------------------ validate

	private void Validate(long? id, JsonObject msg)
	{
		string protocol = Json.Str(msg, "protocol") ?? "";
		var addresses = (Json.Array(msg, "addresses") ?? new JsonArray())
			.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList();

		var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (Json.Object(msg, "options") is JsonObject o)
		{
			foreach (var kv in o)
			{
				if (kv.Value is JsonValue v)
				{
					options[kv.Key] = ScalarToString(v);
				}
			}
		}

		var results = commsEngine.Validate(protocol, addresses, options, Json.Str(msg, "dataType"));

		session.Reply(id, "validateResult", w =>
		{
			w.WriteStartArray("results");

			foreach (var r in results)
			{
				w.WriteStartObject();
				w.WriteBoolean("ok", r.Address != null);

				if (r.Address != null)
				{
					w.WriteString("normalized", r.Address.Normalized);
					w.WriteString("dataType", r.Address.Type.ToString());
					w.WriteBoolean("writable", r.Address.Writable);
				}
				else
				{
					w.WriteString("error", r.Error);
				}

				w.WriteEndObject();
			}

			w.WriteEndArray();
		});
	}

	// ------------------------------------------------------------ status

	private void PushStatus(IReadOnlyList<DeviceStatus> statuses)
	{
		session.Push(Json.Build(w =>
		{
			w.WriteString("t", "status");
			WriteStatuses(w, statuses);
		}));
	}

	private static void WriteStatuses(Utf8JsonWriter w, IReadOnlyList<DeviceStatus> statuses)
	{
		w.WriteStartArray("devices");

		foreach (var s in statuses)
		{
			w.WriteStartObject();
			w.WriteString("name", s.Name);
			w.WriteString("state", s.State.ToString().ToLowerInvariant());

			if (s.LastError != null)
			{
				w.WriteString("lastError", s.LastError);
			}
			else
			{
				w.WriteNull("lastError");
			}

			w.WriteNumber("pollMs", s.PollMs);
			w.WriteNumber("badPoints", s.BadPoints);
			w.WriteEndObject();
		}

		w.WriteEndArray();
	}

	private void Diag(long? id)
	{
		session.Reply(id, "diagResult", w =>
		{
			w.WriteStartObject("server");
			w.WriteString("version", CommsHost.Version);
			w.WriteNumber("uptimeS", (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess()
				.StartTime.ToUniversalTime()).TotalSeconds);
			w.WriteNumber("connections", commsEngine.Workers.Count);
			w.WriteEndObject();

			w.WriteStartArray("devices");

			foreach (var (name, worker) in engine.Devices())
			{
				w.WriteStartObject();
				w.WriteString("name", name);
				w.WriteString("key", worker.Key);
				w.WriteString("state", worker.State.ToString().ToLowerInvariant());
				w.WriteString("lastError", worker.LastError);
				w.WriteNumber("cycles", worker.Cycles);
				w.WriteNumber("pollMsAvg", Math.Round(worker.PollMsAvg, 2));
				w.WriteNumber("pollMsMax", Math.Round(worker.PollMsMax, 2));
				w.WriteNumber("overruns", worker.Overruns);
				w.WriteNumber("requestsPerCycle", worker.RequestsPerCycle);
				w.WriteNumber("points", worker.PointCount);
				w.WriteNumber("activePoints", worker.ActivePointCount);
				w.WriteNumber("badPoints", worker.BadPoints);
				w.WriteNumber("reconnects", worker.Reconnects);
				w.WriteNumber("writeOk", worker.WriteOk);
				w.WriteNumber("writeFail", worker.WriteFail);
				w.WriteStartArray("plan");

				foreach (var line in worker.DescribePlan())
				{
					w.WriteStringValue(line);
				}

				w.WriteEndArray();
				w.WriteEndObject();
			}

			w.WriteEndArray();
		});
	}

	// ------------------------------------------------------------ values

	internal static void WriteValues(Utf8JsonWriter w, string name, IEnumerable<(int Handle, TagValue Value)> values)
	{
		w.WriteStartArray(name);

		foreach (var (h, v) in values)
		{
			w.WriteStartArray();
			w.WriteNumberValue(h);
			WriteValue(w, v.Value);
			w.WriteNumberValue(v.Quality);
			w.WriteNumberValue(v.Timestamp);

			if (v.Status != null || v.Error != null)
			{
				if (v.Status != null)
				{
					w.WriteStringValue(v.Status);
				}
				else
				{
					w.WriteNullValue();
				}

				if (v.Error != null)
				{
					w.WriteStringValue(v.Error);
				}
			}

			w.WriteEndArray();
		}

		w.WriteEndArray();
	}

	/// <summary>
	/// JSON numbers are doubles to most clients, so integers beyond 2^53 go
	/// as strings rather than silently losing their low digits.
	/// </summary>
	internal static void WriteValue(Utf8JsonWriter w, object? value)
	{
		switch (value)
		{
			case null:
				w.WriteNullValue();
				break;
			case bool b:
				w.WriteBooleanValue(b);
				break;
			case long l when Math.Abs((double)l) <= MaxSafeInteger:
				w.WriteNumberValue(l);
				break;
			case long l:
				w.WriteStringValue(l.ToString(CultureInfo.InvariantCulture));
				break;
			case ulong u when u <= MaxSafeInteger:
				w.WriteNumberValue(u);
				break;
			case ulong u:
				w.WriteStringValue(u.ToString(CultureInfo.InvariantCulture));
				break;
			case double d when double.IsFinite(d):
				w.WriteNumberValue(d);
				break;
			case double:
				w.WriteNullValue();
				break;
			case string s:
				w.WriteStringValue(s);
				break;
			default:
				w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
				break;
		}
	}

	public void Dispose()
	{
		session.PullChanges = null;
		engine.ChangesAvailable = null;
		engine.StatusChanged = null;
		engine.Dispose();
	}
}
