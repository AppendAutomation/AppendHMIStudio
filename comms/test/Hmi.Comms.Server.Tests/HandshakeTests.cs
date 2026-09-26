using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Hmi.Comms.Server.Tests;

public sealed class HandshakeTests : IAsyncLifetime
{
	private CommsHost host = null!;
	private IPEndPoint endpoint = null!;
	private const string Token = "test-token-123";

	public Task InitializeAsync()
	{
		var options = ServerOptions.Parse(new[] { "--listen", "127.0.0.1:0", "--token", Token,
			"--allow-shutdown", "--allow-origin", "http://allowed.example" });
		host = new CommsHost(options, Token);
		endpoint = host.Start();

		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		host.RequestShutdown("test done");

		return host.WaitForShutdownAsync();
	}

	private Uri Url(string path = "/v1") => new($"ws://127.0.0.1:{endpoint.Port}{path}");

	[Fact]
	public async Task HelloWithTokenIsAccepted()
	{
		using var ws = await TestClient.ConnectAsync(Url());
		var reply = await TestClient.RequestAsync(ws, new JsonObject
		{
			["t"] = "hello", ["id"] = 1, ["v"] = 1, ["token"] = Token, ["client"] = "test"
		});

		Assert.Equal("hello", (string?)reply["t"]);
		Assert.Equal(1, (int?)reply["id"]);
		Assert.True((bool?)reply["ok"]);
		Assert.Equal(1, (int?)reply["v"]);
	}

	[Fact]
	public async Task PingAfterHelloGetsPong()
	{
		using var ws = await TestClient.OpenAsync(Url(), Token);
		var reply = await TestClient.RequestAsync(ws, new JsonObject { ["t"] = "ping", ["id"] = 7 });

		Assert.Equal("pong", (string?)reply["t"]);
		Assert.Equal(7, (int?)reply["id"]);
	}

	[Fact]
	public async Task WrongTokenClosesWith4001()
	{
		using var ws = await TestClient.ConnectAsync(Url());
		await TestClient.SendAsync(ws, new JsonObject
		{
			["t"] = "hello", ["id"] = 1, ["v"] = 1, ["token"] = "nope"
		});

		var status = await TestClient.WaitForCloseAsync(ws);
		Assert.Equal(4001, (int?)status);
	}

	[Fact]
	public async Task AnythingBeforeHelloClosesWith4001()
	{
		using var ws = await TestClient.ConnectAsync(Url());
		await TestClient.SendAsync(ws, new JsonObject { ["t"] = "ping", ["id"] = 1 });

		var status = await TestClient.WaitForCloseAsync(ws);
		Assert.Equal(4001, (int?)status);
	}

	[Fact]
	public async Task WrongVersionClosesWith4002()
	{
		using var ws = await TestClient.ConnectAsync(Url());
		await TestClient.SendAsync(ws, new JsonObject
		{
			["t"] = "hello", ["id"] = 1, ["v"] = 99, ["token"] = Token
		});

		var status = await TestClient.WaitForCloseAsync(ws);
		Assert.Equal(4002, (int?)status);
	}

	[Fact]
	public async Task UnknownTypeIsAnErrorReply()
	{
		using var ws = await TestClient.OpenAsync(Url(), Token);
		var reply = await TestClient.RequestAsync(ws, new JsonObject { ["t"] = "frobnicate", ["id"] = 3 });

		Assert.Equal("error", (string?)reply["t"]);
		Assert.Equal("bad_request", (string?)reply["code"]);
	}

	[Fact]
	public async Task ForeignOriginIsRefused()
	{
		var ws = new ClientWebSocket();
		ws.Options.SetRequestHeader("Origin", "http://evil.example");

		var e = await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(Url(), default));
		Assert.Contains("403", e.Message);
	}

	[Fact]
	public async Task AllowedOriginIsAccepted()
	{
		var ws = new ClientWebSocket();
		ws.Options.SetRequestHeader("Origin", "http://allowed.example");
		await ws.ConnectAsync(Url(), default);

		Assert.Equal(WebSocketState.Open, ws.State);
	}

	[Fact]
	public async Task WrongPathIs404()
	{
		var ws = new ClientWebSocket();
		var e = await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(Url("/nope"), default));

		Assert.Contains("404", e.Message);
	}

	[Fact]
	public void TokenComparison()
	{
		Assert.True(Session.TokenMatches("abc", "abc"));
		Assert.False(Session.TokenMatches("abd", "abc"));
		Assert.False(Session.TokenMatches("ab", "abc"));
		Assert.False(Session.TokenMatches(null, "abc"));
	}

	[Fact]
	public void OptionsParse()
	{
		var o = ServerOptions.Parse(new[] { "--listen", "0.0.0.0:8765", "--token-stdin",
			"--parent-pid", "42", "--coalesce-ms", "5000" });

		Assert.Equal(IPAddress.Any, o.Host);
		Assert.Equal(8765, o.Port);
		Assert.True(o.TokenFromStdin);
		Assert.Equal(42, o.ParentPid);
		Assert.Equal(1000, o.CoalesceMs);

		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--bogus" }));
		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(
			new[] { "--token", "a", "--token-stdin" }));
	}
}
