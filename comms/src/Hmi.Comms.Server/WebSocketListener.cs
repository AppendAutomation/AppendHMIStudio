using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Hmi.Comms.Core;

namespace Hmi.Comms.Server;

/// <summary>
/// Accepts WebSocket connections on a TcpListener.
///
/// A minimal HTTP/1.1 Upgrade handshake followed by WebSocket.CreateFromStream,
/// both in the BCL, rather than Kestrel: the server stays small, trims well
/// into a single-file executable, and anything that speaks WebSocket -- a
/// browser, Node, the Electron main process -- can connect.
/// </summary>
internal sealed class WebSocketListener
{
	public const string Path = "/v1";
	private const int MaxHeaderBytes = 8192;
	private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

	private readonly TcpListener listener;
	private readonly IReadOnlyList<string> allowedOrigins;

	public WebSocketListener(IPAddress host, int port, IReadOnlyList<string> allowedOrigins)
	{
		listener = new TcpListener(host, port);
		this.allowedOrigins = allowedOrigins;
	}

	public IPEndPoint Start()
	{
		listener.Start();

		return (IPEndPoint)listener.LocalEndpoint;
	}

	public void Stop()
	{
		listener.Stop();
	}

	/// <summary>Accepts until cancelled, handing each upgraded socket to onSocket.</summary>
	public async Task AcceptLoopAsync(Func<WebSocket, EndPoint?, Task> onSocket, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			TcpClient client;

			try
			{
				client = await listener.AcceptTcpClientAsync(ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (SocketException e)
			{
				Log.Warn($"accept failed: {e.Message}");

				continue;
			}

			_ = Task.Run(async () =>
			{
				EndPoint? remote = client.Client.RemoteEndPoint;

				try
				{
					client.NoDelay = true;
					var ws = await UpgradeAsync(client.GetStream(), ct);

					if (ws != null)
					{
						await onSocket(ws, remote);
					}
				}
				catch (Exception e) when (e is IOException or SocketException or
					WebSocketException or OperationCanceledException)
				{
					Log.Debug($"connection {remote} ended: {e.Message}");
				}
				finally
				{
					client.Dispose();
				}
			}, CancellationToken.None);
		}
	}

	/// <summary>
	/// Performs the server side of the opening handshake. Returns null, having
	/// answered with an HTTP error, when the request is not an acceptable
	/// WebSocket upgrade.
	/// </summary>
	internal async Task<WebSocket?> UpgradeAsync(Stream stream, CancellationToken ct)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));

		var request = await ReadHeadersAsync(stream, timeout.Token);

		if (request == null)
		{
			await RespondAsync(stream, 400, "Bad Request", ct);

			return null;
		}

		if (request.Method != "GET" || request.Target != Path)
		{
			await RespondAsync(stream, 404, "Not Found", ct);

			return null;
		}

		request.Headers.TryGetValue("upgrade", out var upgrade);
		request.Headers.TryGetValue("sec-websocket-key", out var key);
		request.Headers.TryGetValue("sec-websocket-version", out var version);

		if (!string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrEmpty(key) || version != "13")
		{
			await RespondAsync(stream, 400, "Bad Request", ct);

			return null;
		}

		// A browser page on any site can open a WebSocket to localhost; only
		// the token stops it from doing anything, and the Origin check stops
		// it from even trying. Non-browser clients send no Origin.
		if (request.Headers.TryGetValue("origin", out var origin) &&
			!IsOriginAllowed(origin, allowedOrigins))
		{
			Log.Warn($"rejected origin {origin}");
			await RespondAsync(stream, 403, "Forbidden", ct);

			return null;
		}

		string accept = Convert.ToBase64String(
			SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid)));

		string response =
			"HTTP/1.1 101 Switching Protocols\r\n" +
			"Upgrade: websocket\r\n" +
			"Connection: Upgrade\r\n" +
			$"Sec-WebSocket-Accept: {accept}\r\n\r\n";

		byte[] bytes = Encoding.ASCII.GetBytes(response);
		await stream.WriteAsync(bytes, ct);

		return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
		{
			IsServer = true,
			KeepAliveInterval = TimeSpan.FromSeconds(20)
		});
	}

	internal static bool IsOriginAllowed(string origin, IReadOnlyList<string> allowed)
	{
		foreach (var a in allowed)
		{
			if (a == "*" || string.Equals(a, origin, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private sealed class HttpRequest
	{
		public string Method = "";
		public string Target = "";
		public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Reads the request line and headers one byte at a time, so nothing past
	/// the blank line -- which belongs to the WebSocket stream -- is consumed.
	/// </summary>
	private static async Task<HttpRequest?> ReadHeadersAsync(Stream stream, CancellationToken ct)
	{
		var buffer = new List<byte>(512);
		var one = new byte[1];

		while (buffer.Count < MaxHeaderBytes)
		{
			int n = await stream.ReadAsync(one, ct);

			if (n == 0)
			{
				return null;
			}

			buffer.Add(one[0]);
			int c = buffer.Count;

			if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n' &&
				buffer[c - 2] == '\r' && buffer[c - 1] == '\n')
			{
				return Parse(Encoding.ASCII.GetString(buffer.ToArray()));
			}
		}

		return null;
	}

	private static HttpRequest? Parse(string text)
	{
		string[] lines = text.Split("\r\n");
		string[] first = lines[0].Split(' ');

		if (first.Length != 3)
		{
			return null;
		}

		var req = new HttpRequest { Method = first[0], Target = first[1] };

		for (int i = 1; i < lines.Length; i++)
		{
			int colon = lines[i].IndexOf(':');

			if (colon > 0)
			{
				req.Headers[lines[i].Substring(0, colon).Trim()] =
					lines[i].Substring(colon + 1).Trim();
			}
		}

		return req;
	}

	private static async Task RespondAsync(Stream stream, int code, string reason, CancellationToken ct)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(
			$"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

		try
		{
			await stream.WriteAsync(bytes, ct);
		}
		catch (IOException)
		{
			// The client went away first; nothing to tell it.
		}
	}
}
