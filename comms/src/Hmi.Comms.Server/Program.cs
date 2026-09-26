using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hmi.Comms.Core;
using Hmi.Comms.Server;

// Startup contract with whatever launched this process: exactly one JSON line
// on stdout -- {"event":"listening",...} or {"event":"fatal",...} -- and
// nothing on stdout after it. Logs go to stderr.

ServerOptions options;

try
{
	options = ServerOptions.Parse(args);
}
catch (HelpRequestedException)
{
	Console.Error.WriteLine(ServerOptions.Usage);

	return 0;
}
catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
{
	return Fatal(e.Message + "\n" + ServerOptions.Usage);
}

Log.Configure(options.LogLevel, options.LogFile);

string? token = options.Token;

if (options.TokenFromStdin)
{
	token = Console.In.ReadLine()?.Trim();

	if (string.IsNullOrEmpty(token))
	{
		return Fatal("--token-stdin: no token on the first line of stdin");
	}
}

// Anything reachable from another machine must be protected.
if (string.IsNullOrEmpty(token))
{
	if (!IPAddress.IsLoopback(options.Host))
	{
		return Fatal("A token is required when listening on a non-loopback address");
	}

	return Fatal("A token is required (--token or --token-stdin)");
}

var engine = new CommsEngine(new IProtocolDriver[]
{
	new Hmi.Comms.Logix.LogixDriver(),
	new Hmi.Comms.Slc.SlcDriver(),
	new Hmi.Comms.Modbus.ModbusDriver()
});

var host = new CommsHost(options, token);

host.Protocols = engine.Protocols;
host.HandlerFactory = session => new DataSession(session, engine);

IPEndPoint endpoint;

try
{
	endpoint = host.Start();
}
catch (Exception e) when (e is System.Net.Sockets.SocketException or UnauthorizedAccessException)
{
	return Fatal($"Cannot listen on {options.Host}:{options.Port}: {e.Message}");
}

using (var stdout = Console.OpenStandardOutput())
using (var w = new Utf8JsonWriter(stdout))
{
	w.WriteStartObject();
	w.WriteString("event", "listening");
	w.WriteNumber("v", 1);
	w.WriteString("host", endpoint.Address.ToString());
	w.WriteNumber("port", endpoint.Port);
	w.WriteNumber("pid", Environment.ProcessId);
	w.WriteString("version", CommsHost.Version);
	w.WriteEndObject();
	w.Flush();
	stdout.WriteByte((byte)'\n');
	stdout.Flush();
}

Log.Info($"hmi-comms {CommsHost.Version} listening on {endpoint}");

if (options.TokenFromStdin)
{
	host.WatchStdin(Console.In);
}

if (options.ParentPid != null)
{
	host.WatchParent(options.ParentPid.Value);
}

using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
	ctx.Cancel = true;
	host.RequestShutdown("SIGTERM");
});

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
	ctx.Cancel = true;
	host.RequestShutdown("SIGINT");
});

await host.WaitForShutdownAsync();
engine.Dispose();
Log.Info("stopped");

return 0;

static int Fatal(string message)
{
	using (var stdout = Console.OpenStandardOutput())
	using (var w = new Utf8JsonWriter(stdout))
	{
		w.WriteStartObject();
		w.WriteString("event", "fatal");
		w.WriteString("message", message);
		w.WriteEndObject();
		w.Flush();
		stdout.WriteByte((byte)'\n');
	}

	Console.Error.WriteLine(message);

	return 2;
}
