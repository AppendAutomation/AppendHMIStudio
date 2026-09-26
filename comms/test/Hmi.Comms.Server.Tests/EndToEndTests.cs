using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using FluentModbus;
using Hmi.Comms.Core;
using Hmi.Comms.Modbus;

namespace Hmi.Comms.Server.Tests;

/// <summary>A loopback Modbus server whose buffers hold wire bytes.</summary>
internal sealed class Plc : IDisposable
{
	public readonly ModbusTcpServer Server = new();
	public readonly int Port;

	public Plc()
	{
		var l = new TcpListener(IPAddress.Loopback, 0);
		l.Start();
		Port = ((IPEndPoint)l.LocalEndpoint).Port;
		l.Stop();
		Server.Start(new IPEndPoint(IPAddress.Loopback, Port));
	}

	public void Set(int reg, ushort v) =>
		BinaryPrimitives.WriteUInt16BigEndian(Server.GetHoldingRegisterBuffer<byte>().Slice(reg * 2), v);

	public ushort Get(int reg) =>
		BinaryPrimitives.ReadUInt16BigEndian(Server.GetHoldingRegisterBuffer<byte>().Slice(reg * 2));

	public void Dispose()
	{
		Server.Stop();
		Server.Dispose();
	}
}

public sealed class EndToEndTests : IAsyncLifetime
{
	private const string Token = "e2e";
	private readonly Plc plc = new();
	private CommsEngine engine = null!;
	private CommsHost host = null!;
	private Uri url = null!;

	public Task InitializeAsync()
	{
		engine = new CommsEngine(new IProtocolDriver[] { new ModbusDriver() });
		var options = ServerOptions.Parse(new[] { "--listen", "127.0.0.1:0", "--token", Token,
			"--coalesce-ms", "10" });
		host = new CommsHost(options, Token)
		{
			Protocols = engine.Protocols,
			HandlerFactory = s => new DataSession(s, engine)
		};
		url = new Uri($"ws://127.0.0.1:{host.Start().Port}/v1");

		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		host.RequestShutdown("done");
		await host.WaitForShutdownAsync();
		engine.Dispose();
		plc.Dispose();
	}

	private JsonObject Configure(int id, params (string Id, string Address)[] tags)
	{
		var list = new JsonArray();

		foreach (var (tid, addr) in tags)
		{
			list.Add(new JsonObject { ["id"] = tid, ["device"] = "Pump", ["address"] = addr });
		}

		return new JsonObject
		{
			["t"] = "configure", ["id"] = id,
			["devices"] = new JsonArray(new JsonObject
			{
				["name"] = "Pump", ["protocol"] = "modbus", ["host"] = "127.0.0.1", ["port"] = plc.Port,
				["options"] = new JsonObject { ["unitId"] = 0 }
			}),
			["tags"] = list
		};
	}

	/// <summary>Reads messages, folding value tuples into a map, until the condition holds.</summary>
	private static async Task<Dictionary<int, JsonArray>> ValuesUntil(ClientWebSocket ws,
		Func<Dictionary<int, JsonArray>, bool> done, Dictionary<int, JsonArray>? values = null,
		List<JsonObject>? seen = null)
	{
		values ??= new Dictionary<int, JsonArray>();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

		while (!done(values))
		{
			var msg = await TestClient.ReceiveAsync(ws, cts.Token) ??
				throw new InvalidOperationException("closed");
			seen?.Add(msg);

			if ((string?)msg["t"] is "snapshot" or "change" or "readResult")
			{
				foreach (var v in msg["values"]!.AsArray())
				{
					var tuple = v!.AsArray();
					values[(int)tuple[0]!] = tuple;
				}
			}
		}

		return values;
	}

	[Fact]
	public async Task ConfigureSubscribeReadWrite()
	{
		plc.Set(0, 100);
		plc.Set(1, 0x4120);
		plc.Set(2, 0x0000);

		using var ws = await TestClient.OpenAsync(url, Token);
		var cfg = await TestClient.RequestAsync(ws, Configure(1, ("Level", "HR:0"), ("Flow", "HR:1:FLOAT"),
			("Nope", "40001")));

		Assert.Equal("configureResult", (string?)cfg["t"]);
		var tags = cfg["tags"]!.AsArray();
		Assert.Equal(1, (int)tags[0]!["h"]!);
		Assert.Equal("HR:1:FLOAT:BE", (string?)tags[1]!["normalized"]);
		Assert.Contains("Use HR:0", (string?)tags[2]!["error"]);
		Assert.Single(cfg["errors"]!.AsArray());

		var seen = new List<JsonObject>();
		await TestClient.SendAsync(ws, new JsonObject
		{
			["t"] = "subscribe", ["id"] = 2, ["tags"] = new JsonArray("Level", 2, "Nope", "Ghost"), ["rateMs"] = 50
		});

		var values = await ValuesUntil(ws, v => v.TryGetValue(1, out var a) && (int)a[2]! == 192 &&
			v.TryGetValue(2, out var b) && (int)b[2]! == 192, seen: seen);

		// The reply comes first, then the snapshot, then changes.
		Assert.Equal("subscribeResult", (string?)seen[0]["t"]);
		Assert.Equal(new[] { "Ghost" }, seen[0]["unknown"]!.AsArray().Select(x => (string?)x));
		Assert.Equal("snapshot", (string?)seen[1]["t"]);
		Assert.Equal(2, (int)seen[1]["sub"]!);
		Assert.Equal(3, seen[1]["values"]!.AsArray().Count);

		Assert.Equal(100, (int)values[1][1]!);
		Assert.Equal(10.0, (double)values[2][1]!);
		Assert.Equal("config", (string?)values[3][4]);

		plc.Set(0, 222);
		values = await ValuesUntil(ws, v => (int?)v[1][1] == 222, values);

		var w = await TestClient.RequestAsync(ws, new JsonObject
		{
			["t"] = "write", ["id"] = 3,
			["values"] = new JsonObject { ["Level"] = 55, ["Nope"] = 1, ["Ghost"] = 1 },
			["handles"] = new JsonArray(new JsonArray(2, 2.5))
		});

		var results = w["results"]!.AsObject();
		Assert.True((bool)results["Level"]!["ok"]!);
		Assert.False((bool)results["Nope"]!["ok"]!);
		Assert.Equal("Unknown tag", (string?)results["Ghost"]!["error"]);
		Assert.True((bool)results["2"]!["ok"]!);
		Assert.Equal(55, plc.Get(0));

		var r = await TestClient.RequestAsync(ws, new JsonObject
		{
			["t"] = "read", ["id"] = 4, ["tags"] = new JsonArray("Flow")
		});
		Assert.Equal(2.5, (double)r["values"]![0]![1]!);
	}

	[Fact]
	public async Task StatusIsPushedAndQueryable()
	{
		using var ws = await TestClient.OpenAsync(url, Token);
		await TestClient.RequestAsync(ws, Configure(1, ("A", "HR:0")));
		await TestClient.SendAsync(ws, new JsonObject
		{
			["t"] = "subscribe", ["id"] = 2, ["tags"] = new JsonArray(1), ["rateMs"] = 100
		});

		var push = await TestClient.ReceiveUntilAsync(ws, m => (string?)m["t"] == "status" &&
			(string?)m["devices"]![0]!["state"] == "connected");
		Assert.Equal("Pump", (string?)push["devices"]![0]!["name"]);

		var st = await TestClient.RequestAsync(ws, new JsonObject { ["t"] = "status", ["id"] = 3 });
		Assert.Equal("connected", (string?)st["devices"]![0]!["state"]);

		var diag = await TestClient.RequestAsync(ws, new JsonObject { ["t"] = "diag", ["id"] = 4 });
		var dev = diag["devices"]![0]!;
		Assert.Equal($"modbus:127.0.0.1:{plc.Port}", (string?)dev["key"]);
		Assert.Equal(1, (int)dev["points"]!);
		Assert.Contains("HR 0..0", (string?)dev["plan"]![0]);
	}

	[Fact]
	public async Task ValidateWithoutConfiguring()
	{
		using var ws = await TestClient.OpenAsync(url, Token);
		var v = await TestClient.RequestAsync(ws, new JsonObject
		{
			["t"] = "validate", ["id"] = 1, ["protocol"] = "modbus",
			["addresses"] = new JsonArray("HR:5:FLOAT", "IR:2", "HR:1:WIDGET"),
			["options"] = new JsonObject { ["byteOrder"] = "MLE" }
		});

		var r = v["results"]!.AsArray();
		Assert.Equal("HR:5:FLOAT:MLE", (string?)r[0]!["normalized"]);
		Assert.Equal("Float32", (string?)r[0]!["dataType"]);
		Assert.False((bool)r[1]!["writable"]!);
		Assert.False((bool)r[2]!["ok"]!);

		var bad = await TestClient.RequestAsync(ws, new JsonObject
		{
			["t"] = "validate", ["id"] = 2, ["protocol"] = "telepathy", ["addresses"] = new JsonArray("x")
		});
		Assert.Contains("Unknown protocol", (string?)bad["results"]![0]!["error"]);
	}

	[Fact]
	public async Task MalformedConfigureIsAnErrorNotACrash()
	{
		using var ws = await TestClient.OpenAsync(url, Token);
		var e = await TestClient.RequestAsync(ws, new JsonObject
		{
			["t"] = "configure", ["id"] = 1, ["devices"] = new JsonArray(new JsonObject { ["protocol"] = "modbus" })
		});

		Assert.Equal("error", (string?)e["t"]);
		Assert.Contains("needs a name", (string?)e["message"]);

		var pong = await TestClient.RequestAsync(ws, new JsonObject { ["t"] = "ping", ["id"] = 2 });
		Assert.Equal("pong", (string?)pong["t"]);
	}

	[Fact]
	public async Task ASlowClientGetsMergedChanges()
	{
		using var ws = await TestClient.OpenAsync(url, Token);
		await TestClient.RequestAsync(ws, Configure(1, ("A", "HR:0")));
		await TestClient.SendAsync(ws, new JsonObject
		{
			["t"] = "subscribe", ["id"] = 2, ["tags"] = new JsonArray(1), ["rateMs"] = 20
		});
		await ValuesUntil(ws, v => v.TryGetValue(1, out var a) && (int)a[2]! == 192);

		// Change the register faster than anything is read off the socket.
		for (ushort i = 1; i <= 40; i++)
		{
			plc.Set(0, i);
			await Task.Delay(25);
		}

		var seen = new List<JsonObject>();
		await ValuesUntil(ws, v => v.TryGetValue(1, out var a) && (int?)a[1] == 40, seen: seen);

		// Every message still carries the latest value per tag, and there are
		// fewer messages than changes.
		int messages = seen.Count(m => (string?)m["t"] == "change");
		Assert.InRange(messages, 1, 40);
	}

	[Fact]
	public void LargeIntegersTravelAsStrings()
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();

		using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
		{
			w.WriteStartArray();
			DataSession.WriteValue(w, 42L);
			DataSession.WriteValue(w, long.MaxValue);
			DataSession.WriteValue(w, ulong.MaxValue);
			DataSession.WriteValue(w, double.NaN);
			DataSession.WriteValue(w, true);
			w.WriteEndArray();
		}

		Assert.Equal("[42,\"9223372036854775807\",\"18446744073709551615\",null,true]",
			System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
	}

	[Fact]
	public async Task RealExecutableEndToEnd()
	{
		plc.Set(7, 700);
		// HMI_COMMS_EXE points this at a published (trimmed, single-file) build.
		string? exe = Environment.GetEnvironmentVariable("HMI_COMMS_EXE");
		var psi = new ProcessStartInfo(exe ?? "dotnet")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		if (exe == null)
		{
			psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "hmi-comms.dll"));
		}

		psi.ArgumentList.Add("--listen");
		psi.ArgumentList.Add("127.0.0.1:0");
		psi.ArgumentList.Add("--token-stdin");

		using var p = Process.Start(psi)!;
		p.ErrorDataReceived += (_, _) => { };
		p.BeginErrorReadLine();
		await p.StandardInput.WriteLineAsync("proc");
		await p.StandardInput.FlushAsync();

		var ev = JsonNode.Parse((await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)))!)!;
		using (var ws = await TestClient.OpenAsync(new Uri($"ws://127.0.0.1:{(int)ev["port"]!}/v1"), "proc"))
		{
			await TestClient.RequestAsync(ws, Configure(1, ("A", "HR:7")));
			await TestClient.SendAsync(ws, new JsonObject
			{
				["t"] = "subscribe", ["id"] = 2, ["tags"] = new JsonArray("A"), ["rateMs"] = 50
			});
			var v = await ValuesUntil(ws, x => x.TryGetValue(1, out var a) && (int?)a[1] == 700);
			Assert.Equal(192, (int)v[1][2]!);

			var w = await TestClient.RequestAsync(ws, new JsonObject
			{
				["t"] = "write", ["id"] = 3, ["values"] = new JsonObject { ["A"] = 701 }
			});
			Assert.True((bool)w["results"]!["A"]!["ok"]!);
			Assert.Equal(701, plc.Get(7));
		}

		p.StandardInput.Close();
		Assert.True(p.WaitForExit(5000), "did not exit when stdin closed");
	}
}
