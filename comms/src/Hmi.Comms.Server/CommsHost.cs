using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Hmi.Comms.Core;

namespace Hmi.Comms.Server;

/// <summary>
/// The server process: listener, sessions and lifetime.
///
/// The process is meant to die with whatever started it. Besides SIGTERM and
/// the shutdown message it watches two things a crashed parent cannot clean
/// up: its own stdin reaching end-of-file (the pipe closes when the parent
/// dies) and, optionally, the parent's process id disappearing.
/// </summary>
internal sealed class CommsHost
{
	public static string Version { get; } =
		typeof(CommsHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion.Split('+')[0] ?? "0.0.0";

	private readonly CancellationTokenSource lifetime = new();
	private readonly ConcurrentDictionary<string, Session> sessions = new();
	private WebSocketListener? listener;

	public ServerOptions Options { get; }
	public string Token { get; }
	public IReadOnlyList<string> Protocols { get; set; } = System.Array.Empty<string>();

	/// <summary>
	/// Devices and tags from --config: every session starts configured with
	/// them. In config mode "file" they are all a session gets.
	/// </summary>
	public (IReadOnlyList<DeviceConfig> Devices, IReadOnlyList<TagConfig> Tags)? FileConfig { get; set; }

	/// <summary>Creates the data plane for an authenticated session.</summary>
	public Func<Session, ISessionHandler?> HandlerFactory { get; set; } = _ => null;

	public CommsHost(ServerOptions options, string token)
	{
		Options = options;
		Token = token;
	}

	public CancellationToken Lifetime => lifetime.Token;

	public ISessionHandler? CreateHandler(Session session) => HandlerFactory(session);

	public IPEndPoint Start()
	{
		listener = new WebSocketListener(Options.Host, Options.Port, Options.AllowedOrigins);
		var endpoint = listener.Start();

		_ = listener.AcceptLoopAsync(async (ws, remote) =>
		{
			var session = new Session(ws, remote, this, lifetime.Token);
			sessions[session.Id] = session;

			try
			{
				await session.RunAsync();
			}
			finally
			{
				sessions.TryRemove(session.Id, out _);

				if (session.Authenticated)
				{
					Log.Info($"session {session.Id} closed");
				}
			}
		}, lifetime.Token);

		return endpoint;
	}

	public int SessionCount => sessions.Count;

	public void RequestShutdown(string reason)
	{
		if (!lifetime.IsCancellationRequested)
		{
			Log.Info($"shutting down: {reason}");
			lifetime.Cancel();
		}
	}

	/// <summary>Exits when stdin closes -- the parent has gone.</summary>
	public void WatchStdin(TextReader stdin)
	{
		var thread = new Thread(() =>
		{
			try
			{
				while (stdin.ReadLine() != null)
				{
					// Anything after the token is ignored.
				}
			}
			catch (IOException)
			{
				// Treated the same as end-of-file.
			}

			RequestShutdown("stdin closed");
		})
		{
			IsBackground = true,
			Name = "stdin-watch"
		};

		thread.Start();
	}

	public void WatchParent(int pid)
	{
		_ = Task.Run(async () =>
		{
			while (!lifetime.IsCancellationRequested)
			{
				if (!IsAlive(pid))
				{
					RequestShutdown($"parent process {pid} exited");

					return;
				}

				try
				{
					await Task.Delay(2000, lifetime.Token);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
		});
	}

	private static bool IsAlive(int pid)
	{
		try
		{
			using var p = Process.GetProcessById(pid);

			return !p.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	public async Task WaitForShutdownAsync()
	{
		try
		{
			await Task.Delay(Timeout.Infinite, lifetime.Token);
		}
		catch (OperationCanceledException)
		{
			// Normal exit path.
		}

		listener?.Stop();

		// Give sessions a moment to close their sockets cleanly.
		var closing = sessions.Values.Select(s => s.CloseAsync(
			System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, "server shutting down"));
		await Task.WhenAny(Task.WhenAll(closing), Task.Delay(2000));
	}
}
