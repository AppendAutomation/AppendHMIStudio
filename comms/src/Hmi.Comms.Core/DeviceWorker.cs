using System.Diagnostics;

namespace Hmi.Comms.Core;

public enum DeviceState
{
	Disconnected,
	Connecting,
	Connected,
	Backoff,
	Idle,
	Disabled
}

/// <summary>
/// Owns one device connection and polls it on its own thread.
///
/// Only points somebody is subscribed to are read, each at the fastest rate
/// any subscriber asked for; points that fall due together are read in one
/// pass of one plan, so a 250 ms tag and a 1 s tag that coincide share their
/// requests. Writes jump the queue. A lost connection marks every point bad at
/// once and is retried with capped, jittered backoff; a connection nobody has
/// needed for a while is closed.
///
/// Step() is one iteration of the loop and is what the tests drive, with a
/// manual clock; Start() runs it on a thread.
/// </summary>
public sealed class DeviceWorker : IDisposable
{
	public static readonly int[] BackoffMs = { 500, 1000, 2000, 4000, 8000, 15000, 30000 };
	public const int IdleDisconnectMs = 30000;
	private const int MaxWaitMs = 60000;
	private const int MaxCachedPlans = 16;

	private readonly object sync = new();
	private readonly Dictionary<string, Point> points = new(StringComparer.Ordinal);
	private readonly Queue<WriteJob> writes = new();
	private readonly List<ReadJob> reads = new();
	private readonly Dictionary<string, IReadPlan> plans = new(StringComparer.Ordinal);
	private readonly Func<long> clock;
	private readonly Func<long> wallClock;
	private readonly AutoResetEvent wakeEvent = new(false);
	private readonly Random random = new();

	private IDeviceConnection? connection;
	private int nextIndex;
	private int backoffStep;
	private long backoffUntil;
	private long lastActive;
	private Thread? thread;
	private volatile bool stopping;
	private IReadPlan? lastPlan;

	public DeviceWorker(string key, IProtocolDriver driver, DeviceConfig config,
		Func<long>? clock = null, Func<long>? wallClock = null)
	{
		Key = key;
		Driver = driver;
		Config = config;

		var sw = Stopwatch.StartNew();
		this.clock = clock ?? (() => sw.ElapsedMilliseconds);
		this.wallClock = wallClock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
	}

	public string Key { get; }
	public IProtocolDriver Driver { get; }
	public DeviceConfig Config { get; }

	/// <summary>Randomise backoff by +/-20%; off in tests.</summary>
	public bool Jitter { get; set; } = true;

	// ------------------------------------------------------------- status

	public DeviceState State { get; private set; } = DeviceState.Disconnected;
	public string? LastError { get; private set; }
	public long LastErrorAt { get; private set; }
	public long Cycles { get; private set; }
	public long Overruns { get; private set; }
	public double PollMsAvg { get; private set; }
	public double PollMsMax { get; private set; }
	public int RequestsPerCycle { get; private set; }
	public int Reconnects { get; private set; }
	public long WriteOk { get; private set; }
	public long WriteFail { get; private set; }
	public int BadPoints { get; private set; }

	/// <summary>Raised on the worker thread when State or LastError changes.</summary>
	public event Action<DeviceWorker>? StateChanged;

	public IReadOnlyList<string> DescribePlan()
	{
		return lastPlan?.Describe() ?? Array.Empty<string>();
	}

	public int PointCount
	{
		get
		{
			lock (sync)
			{
				return points.Count;
			}
		}
	}

	public int ActivePointCount
	{
		get
		{
			lock (sync)
			{
				return points.Values.Count(p => p.RateMs > 0);
			}
		}
	}

	// ------------------------------------------------------------- points

	/// <summary>The point for an address, created on first use; reference counted.</summary>
	public Point Acquire(ParsedAddress address)
	{
		lock (sync)
		{
			if (!points.TryGetValue(address.Key, out var p))
			{
				p = new Point(address, nextIndex++);
				points[address.Key] = p;
				plans.Clear();

				if (!Config.Enabled)
				{
					p.Value = TagValue.Bad(Status.Disabled, "Device is disabled", wallClock());
				}
			}

			p.RefCount++;

			return p;
		}
	}

	public void Release(Point point)
	{
		lock (sync)
		{
			if (--point.RefCount <= 0)
			{
				points.Remove(point.Address.Key);
				plans.Clear();
			}
		}

		Wake();
	}

	/// <summary>Sets how often one subscriber wants a point; 0 unsubscribes.</summary>
	public void Subscribe(Point point, object subscriber, int rateMs)
	{
		lock (sync)
		{
			int before = point.RateMs;
			int rate = rateMs > 0 ? Math.Max(rateMs, Math.Max(10, Config.MinScanMs)) : 0;
			point.SetSubscription(subscriber, rate);

			// Newly wanted, or wanted faster: read it now rather than on the old schedule.
			if (point.RateMs > 0 && (before == 0 || point.RateMs < before))
			{
				point.NextDue = clock();
			}
		}

		Wake();
	}

	// ------------------------------------------------------------- requests

	private sealed class WriteJob
	{
		public required IReadOnlyList<WriteItem> Items;
		public readonly TaskCompletionSource<WriteOutcome[]> Done =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed class ReadJob
	{
		public required HashSet<Point> Points;
		public readonly TaskCompletionSource Done =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	/// <summary>Queues writes; completes once the device has answered.</summary>
	public Task<WriteOutcome[]> WriteAsync(IReadOnlyList<WriteItem> items)
	{
		var job = new WriteJob { Items = items };

		lock (sync)
		{
			writes.Enqueue(job);
		}

		Wake();

		return job.Done.Task;
	}

	/// <summary>Reads these points now, whatever their schedule.</summary>
	public Task ReadNowAsync(IEnumerable<Point> pts)
	{
		var job = new ReadJob { Points = new HashSet<Point>(pts) };

		lock (sync)
		{
			reads.Add(job);
		}

		Wake();

		return job.Done.Task;
	}

	// ------------------------------------------------------------- loop

	public void Start()
	{
		if (thread != null)
		{
			return;
		}

		thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "device:" + Key
		};

		thread.Start();
	}

	public void Wake()
	{
		try
		{
			wakeEvent.Set();
		}
		catch (ObjectDisposedException)
		{
			// Stopped.
		}
	}

	private void Run()
	{
		while (!stopping)
		{
			long wait;

			try
			{
				wait = Step();
			}
			catch (Exception e)
			{
				Log.Error($"{Key}: worker error: {e}");
				wait = 1000;
			}

			if (stopping)
			{
				break;
			}

			wakeEvent.WaitOne((int)Math.Clamp(wait, 0, MaxWaitMs));
		}

		CloseConnection();
	}

	/// <summary>One pass of the loop. Returns how long to wait before the next.</summary>
	public long Step()
	{
		long now = clock();
		List<WriteJob> writeJobs;
		List<ReadJob> readJobs;
		List<Point> active;
		List<Point> due;

		lock (sync)
		{
			writeJobs = new List<WriteJob>(writes);
			writes.Clear();
			readJobs = new List<ReadJob>(reads);
			reads.Clear();

			active = points.Values.Where(p => p.RateMs > 0).ToList();

			var forced = new HashSet<Point>(readJobs.SelectMany(j => j.Points));
			due = points.Values.Where(p => (p.RateMs > 0 && p.NextDue <= now) || forced.Contains(p))
				.OrderBy(p => p.Index).ToList();
		}

		var changed = new List<Point>();

		try
		{
			if (!Config.Enabled)
			{
				SetState(DeviceState.Disabled, null);
				FailWrites(writeJobs, "Device is disabled");
				MarkAll(Status.Disabled, "Device is disabled", changed);

				return MaxWaitMs;
			}

			if (active.Count == 0 && writeJobs.Count == 0 && readJobs.Count == 0)
			{
				return IdleCheck(now);
			}

			lastActive = now;

			if (connection == null)
			{
				if (now < backoffUntil)
				{
					FailWrites(writeJobs, "Device not connected: " + (LastError ?? "connecting"));

					return backoffUntil - now;
				}

				if (!TryConnect(now, changed))
				{
					FailWrites(writeJobs, "Device not connected: " + LastError);

					return backoffUntil - now;
				}
			}

			foreach (var job in writeJobs)
			{
				DoWrite(job, now);

				// Read back what was written in this same pass.
				foreach (var item in job.Items)
				{
					if (!due.Contains(item.Point))
					{
						due.Add(item.Point);
					}
				}
			}

			due.Sort((a, b) => a.Index.CompareTo(b.Index));

			if (due.Count > 0)
			{
				DoRead(due, now, changed);
			}

			return NextWait(now);
		}
		catch (Exception e) when (IsTransportFailure(e))
		{
			OnConnectionLost(e, now, changed);
			FailWrites(writeJobs, "Connection lost: " + Errors.Describe(e));

			return backoffUntil - now;
		}
		finally
		{
			foreach (var job in readJobs)
			{
				job.Done.TrySetResult();
			}

			Notify(changed);
		}
	}

	private long IdleCheck(long now)
	{
		if (connection != null)
		{
			long idleFor = now - lastActive;

			if (idleFor >= IdleDisconnectMs)
			{
				CloseConnection();
				SetState(DeviceState.Idle, null);

				return MaxWaitMs;
			}

			return IdleDisconnectMs - idleFor;
		}

		return MaxWaitMs;
	}

	private bool TryConnect(long now, List<Point> changed)
	{
		SetState(DeviceState.Connecting, LastError);

		IDeviceConnection? conn = null;

		try
		{
			conn = Driver.Create(Config);
			conn.Connect();
			connection = conn;

			if (backoffStep > 0 || Cycles > 0)
			{
				Reconnects++;
			}

			backoffStep = 0;
			SetState(DeviceState.Connected, null);
			Log.Info($"{Config.Name} ({Key}): connected");

			lock (sync)
			{
				// Everything is due again: values held since the outage are stale.
				foreach (var p in points.Values)
				{
					p.NextDue = now;
				}
			}

			return true;
		}
		catch (Exception e)
		{
			conn?.Dispose();
			StartBackoff(now, Errors.Describe(e));
			MarkAll(Status.Comm, Errors.Describe(e), changed);

			return false;
		}
	}

	private void DoWrite(WriteJob job, long now)
	{
		WriteOutcome[] results;

		try
		{
			results = connection!.Write(job.Items);
		}
		catch (Exception e) when (IsTransportFailure(e))
		{
			job.Done.TrySetResult(job.Items.Select(_ => WriteOutcome.Fail("Connection lost: " +
				Errors.Describe(e))).ToArray());
			WriteFail += job.Items.Count;

			throw;
		}

		lock (sync)
		{
			// Read back what was written, promptly.
			foreach (var item in job.Items)
			{
				item.Point.NextDue = now;
			}
		}

		WriteOk += results.Count(r => r.Ok);
		WriteFail += results.Count(r => !r.Ok);
		job.Done.TrySetResult(results);
	}

	private void DoRead(List<Point> due, long now, List<Point> changed)
	{
		string key = string.Join(",", due.Select(p => p.Index));
		IReadPlan? plan;

		lock (sync)
		{
			if (!plans.TryGetValue(key, out plan))
			{
				if (plans.Count >= MaxCachedPlans)
				{
					plans.Clear();
				}

				plan = connection!.Plan(due);
				plans[key] = plan;
			}
		}

		lastPlan = plan;
		var sink = new Sink(this, changed);
		var sw = Stopwatch.StartNew();

		connection!.Read(plan, sink);

		double ms = sw.Elapsed.TotalMilliseconds;
		Cycles++;
		PollMsAvg = Cycles == 1 ? ms : PollMsAvg * 0.9 + ms * 0.1;
		PollMsMax = Math.Max(PollMsMax, ms);
		RequestsPerCycle = plan.RequestCount;

		int fastest = int.MaxValue;

		lock (sync)
		{
			foreach (var p in due)
			{
				// A forced read of a point not yet due leaves its schedule alone.
				if (p.RateMs > 0 && p.NextDue <= now)
				{
					fastest = Math.Min(fastest, p.RateMs);

					// Next read on the next multiple of the rate. Aligning to the
					// clock rather than to when the point was subscribed keeps
					// points of one rate in phase -- they arrive a few ms apart
					// but are read together from then on -- and puts a 250 ms
					// point's reads on its 1 s neighbours' too. Falling behind
					// skips missed slots rather than bursting to catch up.
					p.NextDue = (now / p.RateMs + 1) * p.RateMs;
				}
			}

			BadPoints = points.Values.Count(p => !p.Value.IsGood);
		}

		if (fastest != int.MaxValue && ms > fastest)
		{
			Overruns++;
		}
	}

	private long NextWait(long now)
	{
		lock (sync)
		{
			long next = long.MaxValue;

			foreach (var p in points.Values)
			{
				if (p.RateMs > 0)
				{
					next = Math.Min(next, p.NextDue);
				}
			}

			if (writes.Count > 0 || reads.Count > 0)
			{
				return 0;
			}

			return next == long.MaxValue ? IdleDisconnectMs : Math.Max(0, next - now);
		}
	}

	private void OnConnectionLost(Exception e, long now, List<Point> changed)
	{
		string error = Errors.Describe(e);
		Log.Warn($"{Config.Name} ({Key}): connection lost: {error}");
		CloseConnection();
		StartBackoff(now, error);
		MarkAll(Status.Comm, error, changed);
	}

	private void StartBackoff(long now, string error)
	{
		int delay = BackoffMs[Math.Min(backoffStep, BackoffMs.Length - 1)];

		if (Jitter)
		{
			delay = (int)(delay * (0.8 + random.NextDouble() * 0.4));
		}

		backoffStep++;
		backoffUntil = now + delay;
		LastErrorAt = wallClock();
		SetState(DeviceState.Backoff, error);
	}

	private void MarkAll(string status, string error, List<Point> changed)
	{
		long ts = wallClock();

		lock (sync)
		{
			foreach (var p in points.Values)
			{
				var v = TagValue.Bad(status, error, ts);

				if (!p.Value.SameAs(v))
				{
					p.Value = v;
					changed.Add(p);
				}
			}

			BadPoints = points.Count;
		}
	}

	private static void FailWrites(List<WriteJob> jobs, string error)
	{
		foreach (var job in jobs)
		{
			job.Done.TrySetResult(job.Items.Select(_ => WriteOutcome.Fail(error)).ToArray());
		}
	}

	private void SetState(DeviceState state, string? error)
	{
		if (State == state && LastError == error)
		{
			return;
		}

		State = state;
		LastError = error;
		StateChanged?.Invoke(this);
	}

	private static void Notify(List<Point> changed)
	{
		foreach (var p in changed)
		{
			foreach (var l in p.Listeners())
			{
				try
				{
					l.OnPointChanged(p);
				}
				catch (Exception e)
				{
					Log.Error($"listener failed: {e}");
				}
			}
		}
	}

	internal static bool IsTransportFailure(Exception e)
	{
		return e is CommsLostException or IOException or System.Net.Sockets.SocketException or
			TimeoutException or ObjectDisposedException or InvalidOperationException;
	}

	private void CloseConnection()
	{
		var conn = connection;
		connection = null;

		try
		{
			conn?.Dispose();
		}
		catch (Exception e)
		{
			Log.Debug($"{Key}: closing: {e.Message}");
		}
	}

	public void Dispose()
	{
		stopping = true;
		Wake();

		if (thread != null && thread != Thread.CurrentThread)
		{
			thread.Join(3000);
		}
		else
		{
			CloseConnection();
		}

		lock (sync)
		{
			foreach (var job in writes)
			{
				job.Done.TrySetResult(job.Items.Select(_ => WriteOutcome.Fail("Server stopping"))
					.ToArray());
			}

			writes.Clear();

			foreach (var job in reads)
			{
				job.Done.TrySetResult();
			}

			reads.Clear();
		}

		wakeEvent.Dispose();
	}

	private sealed class Sink : IReadSink
	{
		private readonly DeviceWorker worker;
		private readonly List<Point> changed;
		private readonly long ts;

		public Sink(DeviceWorker worker, List<Point> changed)
		{
			this.worker = worker;
			this.changed = changed;
			ts = worker.wallClock();
		}

		public void Set(Point point, object? raw)
		{
			Update(point, TagValue.Good(raw, ts));
		}

		public void Fail(Point point, string status, string error)
		{
			Update(point, TagValue.Bad(status, error, ts));
		}

		private void Update(Point point, TagValue v)
		{
			if (!point.Value.SameAs(v))
			{
				point.Value = v;
				changed.Add(point);
			}
		}
	}
}
