using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Hmi.Comms.Server.Tests;

/// <summary>A small WebSocket JSON client for tests.</summary>
internal static class TestClient
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	public static async Task<ClientWebSocket> ConnectAsync(Uri url)
	{
		var ws = new ClientWebSocket();
		await ws.ConnectAsync(url, default);

		return ws;
	}

	/// <summary>Connects and completes hello.</summary>
	public static async Task<ClientWebSocket> OpenAsync(Uri url, string token)
	{
		var ws = await ConnectAsync(url);
		var reply = await RequestAsync(ws, new JsonObject
		{
			["t"] = "hello", ["id"] = 0, ["v"] = 1, ["token"] = token, ["client"] = "test"
		});

		if ((bool?)reply["ok"] != true)
		{
			throw new InvalidOperationException("hello failed: " + reply.ToJsonString());
		}

		return ws;
	}

	public static Task SendAsync(ClientWebSocket ws, JsonObject msg)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());

		return ws.SendAsync(bytes, WebSocketMessageType.Text, true, default);
	}

	/// <summary>Sends a request and returns the reply with the same id.</summary>
	public static async Task<JsonObject> RequestAsync(ClientWebSocket ws, JsonObject msg)
	{
		long id = long.Parse(msg["id"]!.ToJsonString());
		await SendAsync(ws, msg);

		return await ReceiveUntilAsync(ws, m => (long?)m["id"] == id);
	}

	public static async Task<JsonObject> ReceiveUntilAsync(ClientWebSocket ws, Func<JsonObject, bool> match,
		TimeSpan? timeout = null)
	{
		using var cts = new CancellationTokenSource(timeout ?? Timeout);

		while (true)
		{
			var msg = await ReceiveAsync(ws, cts.Token);

			if (msg == null)
			{
				throw new InvalidOperationException("socket closed while waiting");
			}

			if (match(msg))
			{
				return msg;
			}
		}
	}

	public static async Task<JsonObject?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
	{
		var buffer = new byte[64 * 1024];
		using var ms = new MemoryStream();
		WebSocketReceiveResult r;

		do
		{
			r = await ws.ReceiveAsync(buffer, ct);

			if (r.MessageType == WebSocketMessageType.Close)
			{
				return null;
			}

			ms.Write(buffer, 0, r.Count);
		}
		while (!r.EndOfMessage);

		return JsonNode.Parse(ms.ToArray()) as JsonObject;
	}

	/// <summary>Reads until the server closes; returns its close status.</summary>
	public static async Task<int?> WaitForCloseAsync(ClientWebSocket ws)
	{
		using var cts = new CancellationTokenSource(Timeout);

		try
		{
			while (await ReceiveAsync(ws, cts.Token) != null)
			{
			}
		}
		catch (WebSocketException)
		{
			// Closed abruptly after the close frame.
		}

		return (int?)ws.CloseStatus;
	}
}
