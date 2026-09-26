namespace Hmi.Comms.Core;

/// <summary>
/// The server's data plane: the protocol drivers and one worker per device
/// connection, shared by every client session. Two sessions naming the same
/// PLC get the same connection, and the same address is read once for both.
/// </summary>
public sealed class CommsEngine : IDisposable
{
	private readonly Dictionary<string, IProtocolDriver> drivers = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, WorkerEntry> workers = new(StringComparer.Ordinal);
	private readonly object sync = new();

	public CommsEngine(IEnumerable<IProtocolDriver> drivers)
	{
		foreach (var d in drivers)
		{
			this.drivers[d.Protocol] = d;
		}
	}

	/// <summary>Monotonic clock for scheduling (ms); replaced in tests.</summary>
	public Func<long>? Clock { get; set; }

	/// <summary>Wall clock for timestamps (UTC ms); replaced in tests.</summary>
	public Func<long>? WallClock { get; set; }

	/// <summary>False in tests, which drive DeviceWorker.Step themselves.</summary>
	public bool StartWorkers { get; set; } = true;

	/// <summary>Backoff jitter for new workers; off in tests.</summary>
	public bool Jitter { get; set; } = true;

	public IReadOnlyList<string> Protocols => drivers.Keys.OrderBy(k => k).ToList();

	public IProtocolDriver? Driver(string protocol)
	{
		return drivers.TryGetValue(protocol ?? "", out var d) ? d : null;
	}

	public EngineSession CreateSession() => new(this);

	/// <summary>Raised (on a worker thread) when any device changes state.</summary>
	public event Action<DeviceWorker>? DeviceStateChanged;

	private sealed class WorkerEntry
	{
		public required DeviceWorker Worker;
		public int Refs;
	}

	internal DeviceWorker AcquireWorker(IProtocolDriver driver, DeviceConfig device)
	{
		string key = driver.Protocol + ":" + driver.ConnectionKey(device);

		lock (sync)
		{
			if (!workers.TryGetValue(key, out var entry))
			{
				var worker = new DeviceWorker(key, driver, device, Clock, WallClock) { Jitter = Jitter };
				worker.StateChanged += w => DeviceStateChanged?.Invoke(w);
				entry = new WorkerEntry { Worker = worker };
				workers[key] = entry;

				if (StartWorkers)
				{
					worker.Start();
				}
			}

			entry.Refs++;

			return entry.Worker;
		}
	}

	internal void ReleaseWorker(DeviceWorker worker)
	{
		lock (sync)
		{
			if (!workers.TryGetValue(worker.Key, out var entry) || entry.Worker != worker)
			{
				return;
			}

			if (--entry.Refs > 0)
			{
				return;
			}

			workers.Remove(worker.Key);
		}

		// Joining the thread can take a moment; never on the caller's thread.
		Task.Run(worker.Dispose);
	}

	public IReadOnlyList<DeviceWorker> Workers
	{
		get
		{
			lock (sync)
			{
				return workers.Values.Select(e => e.Worker).ToList();
			}
		}
	}

	/// <summary>Checks addresses for a protocol without configuring anything.</summary>
	public IReadOnlyList<ParseResult> Validate(string protocol, IReadOnlyList<string> addresses,
		IReadOnlyDictionary<string, string>? options = null, string? dataType = null)
	{
		var driver = Driver(protocol);

		if (driver == null)
		{
			return addresses.Select(_ => ParseResult.Fail($"Unknown protocol '{protocol}'")).ToList();
		}

		var device = new DeviceConfig
		{
			Name = "validate",
			Protocol = protocol,
			Host = "0.0.0.0",
			Options = options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		};

		return addresses.Select(a => driver.Parse(device, a, dataType)).ToList();
	}

	public void Dispose()
	{
		List<DeviceWorker> all;

		lock (sync)
		{
			all = workers.Values.Select(e => e.Worker).ToList();
			workers.Clear();
		}

		Parallel.ForEach(all, w => w.Dispose());
	}
}

/// <summary>A device's state as a session sees it, under the session's name for it.</summary>
public sealed record DeviceStatus(string Name, DeviceState State, string? LastError,
	double PollMs, int BadPoints);

public sealed record ConfiguredTag(string Id, int Handle, string? Normalized, string? Error);
