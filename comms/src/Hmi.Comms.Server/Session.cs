using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Hmi.Comms.Core;

namespace Hmi.Comms.Server;

/// <summary>
/// One client connection.
///
/// Nothing but hello is accepted until the client has presented the token:
/// browsers cannot set headers on a WebSocket, so authentication is the first
/// message rather than part of the upgrade.
///
/// All sends go through one writer loop, because a WebSocket allows only one
/// outstanding send. Replies are queued ahead of value changes, so a burst of
/// changes can never delay a write result.
/// </summary>
internal sealed class Session
{
	public const int ProtocolVersion = 1;
	public const int CloseBadToken = 4001;
	public const int CloseBadVersion = 4002;
	private const int MaxMessageBytes = 4 * 1024 * 1024;

	private static int counter;

	private readonly WebSocket socket;
	private readonly CommsHost host;
	private readonly ConcurrentQueue<byte[]> replies = new();
	private readonly SemaphoreSlim wake = new(0, 1);
	private volatile bool changesPending;
	private readonly CancellationTokenSource cts;

	public string Id { get; } = "s" + Interlocked.Increment(ref counter);

	public CommsHost Host => host;
	public EndPoint? Remote { get; }
	public bool Authenticated { get; private set; }

	/// <summary>Filled in when a data plane is attached (see CommsHost).</summary>
	public ISessionHandler? Handler { get; set; }

	public Session(WebSocket socket, EndPoint? remote, CommsHost host, CancellationToken hostToken)
	{
		this.socket = socket;
		this.host = host;
		Remote = remote;
		cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
	}

	public CancellationToken Token => cts.Token;

	public async Task RunAsync()
	{
		var writer = Task.Run(WriteLoopAsync);
		var authTimeout = Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ContinueWith(t =>
		{
			if (!t.IsCanceled && !Authenticated)
			{
				_ = CloseAsync(CloseBadToken, "hello required");
			}
		}, TaskScheduler.Default);

		try
		{
			await ReadLoopAsync();
		}
		finally
		{
			cts.Cancel();
			Handler?.Dispose();

			try
			{
				await writer;
			}
			catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
			{
				// Closing; the peer may already be gone.
			}
		}
	}

	private async Task ReadLoopAsync()
	{
		var buffer = new ArrayBufferWriter<byte>(4096);

		while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
		{
			buffer.Clear();
			ValueWebSocketReceiveResult result;

			do
			{
				var memory = buffer.GetMemory(4096);
				result = await socket.ReceiveAsync(memory, cts.Token);

				if (result.MessageType == WebSocketMessageType.Close)
				{
					return;
				}

				buffer.Advance(result.Count);

				if (buffer.WrittenCount > MaxMessageBytes)
				{
					await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big");

					return;
				}
			}
			while (!result.EndOfMessage);

			if (result.MessageType != WebSocketMessageType.Text)
			{
				SendError(null, "bad_request", "Only text frames are accepted");

				continue;
			}

			JsonObject? msg = null;

			try
			{
				msg = JsonNode.Parse(buffer.WrittenSpan) as JsonObject;
			}
			catch (JsonException e)
			{
				SendError(null, "bad_json", e.Message);

				continue;
			}

			if (msg == null)
			{
				SendError(null, "bad_request", "Expected a JSON object");

				continue;
			}

			await DispatchAsync(msg);
		}
	}

	private async Task DispatchAsync(JsonObject msg)
	{
		string? type = Json.Str(msg, "t");
		long? id = Json.Long(msg, "id");

		if (!Authenticated)
		{
			if (type == "hello")
			{
				await HelloAsync(msg, id);
			}
			else
			{
				await CloseAsync(CloseBadToken, "hello required");
			}

			return;
		}

		try
		{
			switch (type)
			{
				case "hello":
					SendError(id, "bad_request", "Already authenticated");
					break;
				case "ping":
					Reply(id, "pong", w => { });
					break;
				case "shutdown":
					if (!host.Options.AllowShutdown)
					{
						SendError(id, "forbidden", "Shutdown is not enabled on this server");
					}
					else
					{
						Reply(id, "shutdownResult", w => w.WriteBoolean("ok", true));
						host.RequestShutdown("shutdown message");
					}

					break;
				default:
					if (Handler == null || !await Handler.HandleAsync(type ?? "", id, msg))
					{
						SendError(id, "bad_request", $"Unknown message type '{type}'");
					}

					break;
			}
		}
		catch (ProtocolException e)
		{
			SendError(id, e.Code, e.Message);
		}
	}

	private async Task HelloAsync(JsonObject msg, long? id)
	{
		int v = (int)(Json.Long(msg, "v") ?? 0);

		if (v != ProtocolVersion)
		{
			await CloseAsync(CloseBadVersion, $"unsupported protocol version {v}");

			return;
		}

		if (!TokenMatches(Json.Str(msg, "token"), host.Token))
		{
			Log.Warn($"session {Id} from {Remote}: bad token");
			await CloseAsync(CloseBadToken, "bad token");

			return;
		}

		Authenticated = true;
		Handler = host.CreateHandler(this);
		Log.Info($"session {Id} opened ({Json.Str(msg, "client") ?? "unknown client"})");

		Reply(id, "hello", w =>
		{
			w.WriteBoolean("ok", true);
			w.WriteNumber("v", ProtocolVersion);
			w.WriteString("server", "hmi-comms/" + CommsHost.Version);
			w.WriteString("session", Id);
			w.WriteString("configMode", host.Options.ConfigMode);
			w.WriteStartArray("protocols");

			foreach (var p in host.Protocols)
			{
				w.WriteStringValue(p);
			}

			w.WriteEndArray();
		});
	}

	/// <summary>Constant time, so the comparison leaks nothing about the token.</summary>
	internal static bool TokenMatches(string? given, string? expected)
	{
		if (given == null || expected == null)
		{
			return false;
		}

		return CryptographicOperations.FixedTimeEquals(
			Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
	}

	// ------------------------------------------------------------ sending

	/// <summary>Queues a reply: {"t": type, "id": id, ...body}.</summary>
	public void Reply(long? id, string type, Action<Utf8JsonWriter> body)
	{
		Push(Json.Build(w =>
		{
			w.WriteString("t", type);

			if (id != null)
			{
				w.WriteNumber("id", id.Value);
			}

			body(w);
		}));
	}

	/// <summary>Queues a complete message, ahead of any pending changes.</summary>
	public void Push(byte[] message)
	{
		replies.Enqueue(message);
		Wake();
	}

	public void SendError(long? id, string code, string message)
	{
		Reply(id, "error", w =>
		{
			w.WriteString("code", code);
			w.WriteString("message", message);
		});
	}

	/// <summary>
	/// Called by the writer loop to collect every change pending since the
	/// last call, as one message. Latest value wins per tag, so a slow client
	/// gets fewer, merged updates rather than a growing queue.
	/// </summary>
	public Func<byte[]?>? PullChanges { get; set; }

	/// <summary>Tells the writer loop that changes are waiting.</summary>
	public void SignalChanges()
	{
		changesPending = true;
		Wake();
	}

	private void Wake()
	{
		try
		{
			if (wake.CurrentCount == 0)
			{
				wake.Release();
			}
		}
		catch (SemaphoreFullException)
		{
			// Already signalled by a racing caller.
		}
	}

	private async Task WriteLoopAsync()
	{
		while (!cts.IsCancellationRequested)
		{
			await wake.WaitAsync(cts.Token);

			while (replies.TryDequeue(out var reply))
			{
				await SendAsync(reply);
			}

			if (changesPending && PullChanges != null)
			{
				// Let a burst of changes from one poll cycle land in one message.
				if (host.Options.CoalesceMs > 0)
				{
					await Task.Delay(host.Options.CoalesceMs, cts.Token);
				}

				while (replies.TryDequeue(out var reply))
				{
					await SendAsync(reply);
				}

				changesPending = false;
				var batch = PullChanges();

				if (batch != null)
				{
					await SendAsync(batch);
				}
			}
		}
	}

	private async Task SendAsync(byte[] message)
	{
		if (socket.State != WebSocketState.Open)
		{
			return;
		}

		await socket.SendAsync(message, WebSocketMessageType.Text, true, cts.Token);
	}

	public async Task CloseAsync(int code, string reason)
	{
		await CloseAsync((WebSocketCloseStatus)code, reason);
	}

	public async Task CloseAsync(WebSocketCloseStatus code, string reason)
	{
		try
		{
			if (socket.State == WebSocketState.Open)
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await socket.CloseAsync(code, reason, timeout.Token);
			}
		}
		catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
		{
			// Already gone.
		}
		finally
		{
			cts.Cancel();
		}
	}
}

/// <summary>A request the server understood but refuses; becomes an error reply.</summary>
internal sealed class ProtocolException : Exception
{
	public string Code { get; }

	public ProtocolException(string code, string message) : base(message)
	{
		Code = code;
	}
}

/// <summary>The data plane of a session: configure, subscribe, read, write...</summary>
internal interface ISessionHandler : IDisposable
{
	/// <returns>False when the message type is not one it handles.</returns>
	Task<bool> HandleAsync(string type, long? id, JsonObject msg);
}
