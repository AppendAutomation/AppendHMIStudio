using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Hmi.Comms.Server.Tests;

/// <summary>
/// Runs the real executable the way the Electron main process does: token on
/// stdin, one JSON line back on stdout, and exit when stdin closes.
/// </summary>
public sealed class ProcessTests
{
	private static string ServerDll()
	{
		// The test project references the server, so its build output sits
		// beside the test assembly.
		string dir = AppContext.BaseDirectory;
		string dll = Path.Combine(dir, "hmi-comms.dll");

		Assert.True(File.Exists(dll), "hmi-comms.dll not found beside the tests");

		return dll;
	}

	private static Process Start(params string[] args)
	{
		var psi = new ProcessStartInfo("dotnet")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		psi.ArgumentList.Add(ServerDll());

		foreach (var a in args)
		{
			psi.ArgumentList.Add(a);
		}

		var p = Process.Start(psi)!;
		p.ErrorDataReceived += (_, _) => { };
		p.BeginErrorReadLine();

		return p;
	}

	[Fact]
	public async Task ListensThenExitsWhenStdinCloses()
	{
		using var p = Start("--listen", "127.0.0.1:0", "--token-stdin", "--allow-shutdown");
		await p.StandardInput.WriteLineAsync("proc-token");
		await p.StandardInput.FlushAsync();

		string? line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
		var ev = JsonNode.Parse(line!)!.AsObject();

		Assert.Equal("listening", (string?)ev["event"]);
		int port = (int)ev["port"]!;
		Assert.True(port > 0);

		using (var ws = await TestClient.OpenAsync(new Uri($"ws://127.0.0.1:{port}/v1"), "proc-token"))
		{
			var pong = await TestClient.RequestAsync(ws, new JsonObject { ["t"] = "ping", ["id"] = 2 });
			Assert.Equal("pong", (string?)pong["t"]);
		}

		p.StandardInput.Close();

		Assert.True(p.WaitForExit(5000), "server did not exit after stdin closed");
		Assert.Equal(0, p.ExitCode);
	}

	[Fact]
	public async Task ShutdownMessageStopsTheServer()
	{
		using var p = Start("--listen", "127.0.0.1:0", "--token", "t2", "--allow-shutdown");
		string? line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
		int port = (int)JsonNode.Parse(line!)!["port"]!;

		using var ws = await TestClient.OpenAsync(new Uri($"ws://127.0.0.1:{port}/v1"), "t2");
		var reply = await TestClient.RequestAsync(ws, new JsonObject { ["t"] = "shutdown", ["id"] = 5 });

		Assert.Equal("shutdownResult", (string?)reply["t"]);
		Assert.True(p.WaitForExit(5000), "server did not exit after shutdown");
	}

	[Fact]
	public async Task MissingTokenIsFatal()
	{
		using var p = Start("--listen", "127.0.0.1:0");
		string? line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal("fatal", (string?)JsonNode.Parse(line!)!["event"]);
		Assert.True(p.WaitForExit(5000));
		Assert.Equal(2, p.ExitCode);
	}
}
