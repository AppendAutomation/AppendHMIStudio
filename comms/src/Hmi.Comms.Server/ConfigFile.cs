using System.Text.Json.Nodes;
using Hmi.Comms.Core;

namespace Hmi.Comms.Server;

/// <summary>
/// The --config file: the same devices and tags a configure message carries,
/// plus an optional token.
///
///   { "token": "...", "devices": [ ... ], "tags": [ ... ] }
/// </summary>
internal static class ConfigFile
{
	public static (List<DeviceConfig> Devices, List<TagConfig> Tags, string? Token) Load(string path)
	{
		var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ??
			throw new ProtocolException("bad_config", "The file must hold a JSON object");

		var devices = new List<DeviceConfig>();
		var tags = new List<TagConfig>();

		foreach (var node in Json.Array(root, "devices") ?? new JsonArray())
		{
			devices.Add(DataSession.ParseDevice(node as JsonObject ??
				throw new ProtocolException("bad_config", "Each device must be an object")));
		}

		foreach (var node in Json.Array(root, "tags") ?? new JsonArray())
		{
			tags.Add(DataSession.ParseTag(node as JsonObject ??
				throw new ProtocolException("bad_config", "Each tag must be an object")));
		}

		return (devices, tags, Json.Str(root, "token"));
	}
}
