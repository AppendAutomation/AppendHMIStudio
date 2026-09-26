namespace Hmi.Comms.Core;

/// <summary>
/// One client's view of the engine: its devices and tags, under its own names,
/// mapped to shared workers and points.
///
/// Tags are given integer handles so change traffic stays small. Scaling and
/// deadband belong to the tag, not the point: two clients can read the same
/// register in different units. Changes accumulate in a latest-value-wins map
/// until the transport drains them, so a slow client gets merged updates and
/// memory is bounded by the tag count.
/// </summary>
public sealed class EngineSession : IPointListener, IDisposable
{
	private readonly CommsEngine engine;
	private readonly object sync = new();

	private List<SessionTag> tags = new();
	private Dictionary<string, SessionTag> byId = new(StringComparer.Ordinal);
	private Dictionary<Point, List<SessionTag>> byPoint = new();
	private Dictionary<string, DeviceWorker> deviceWorkers = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<DeviceWorker> heldWorkers = new();
	private readonly HashSet<int> subscribed = new();
	private readonly Dictionary<int, int> tagRates = new();
	private readonly Dictionary<int, TagValue> pending = new();
	private int rateMs;
	private bool disposed;

	internal EngineSession(CommsEngine engine)
	{
		this.engine = engine;
		engine.DeviceStateChanged += OnDeviceStateChanged;
	}

	/// <summary>Called (on a worker thread) when changes are waiting to be drained.</summary>
	public Action? ChangesAvailable { get; set; }

	/// <summary>Called (on a worker thread) with this session's statuses for a device that changed.</summary>
	public Action<IReadOnlyList<DeviceStatus>>? StatusChanged { get; set; }

	private sealed class SessionTag
	{
		public required int Handle;
		public required TagConfig Config;
		public DeviceWorker? Worker;
		public Point? Point;
		public string? Error;
		public TagValue? LastSent;
	}

	// ------------------------------------------------------------ configure

	/// <summary>Replaces this session's devices and tags. Bad tags still get a handle.</summary>
	public IReadOnlyList<ConfiguredTag> Configure(IReadOnlyList<DeviceConfig> devices,
		IReadOnlyList<TagConfig> tagConfigs)
	{
		lock (sync)
		{
			// The new set is acquired before the old is released, so a device or
			// address present in both keeps its connection and its last value.
			var old = (Tags: tags, Workers: heldWorkers.ToList());
			tags = new List<SessionTag>();
			byId = new Dictionary<string, SessionTag>(StringComparer.Ordinal);
			byPoint = new Dictionary<Point, List<SessionTag>>();
			deviceWorkers = new Dictionary<string, DeviceWorker>(StringComparer.OrdinalIgnoreCase);
			heldWorkers.Clear();
			subscribed.Clear();
			pending.Clear();

			var deviceErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var deviceConfigs = new Dictionary<string, (DeviceConfig Config, IProtocolDriver Driver)>(
				StringComparer.OrdinalIgnoreCase);

			foreach (var d in devices)
			{
				if (deviceConfigs.ContainsKey(d.Name) || deviceErrors.ContainsKey(d.Name))
				{
					deviceErrors[d.Name] = $"Device '{d.Name}' is defined twice";
					deviceConfigs.Remove(d.Name);

					continue;
				}

				var driver = engine.Driver(d.Protocol);

				if (driver == null)
				{
					deviceErrors[d.Name] = $"Unknown protocol '{d.Protocol}'";

					continue;
				}

				string? error = driver.ValidateDevice(d);

				if (error != null)
				{
					deviceErrors[d.Name] = error;

					continue;
				}

				deviceConfigs[d.Name] = (d, driver);
			}

			var result = new List<ConfiguredTag>(tagConfigs.Count);
			int handle = 0;

			foreach (var tc in tagConfigs)
			{
				var tag = new SessionTag { Handle = ++handle, Config = tc };

				if (byId.ContainsKey(tc.Id))
				{
					tag.Error = $"Tag '{tc.Id}' is defined twice";
				}
				else if (deviceErrors.TryGetValue(tc.Device ?? "", out var derr))
				{
					tag.Error = derr;
				}
				else if (!deviceConfigs.TryGetValue(tc.Device ?? "", out var dc))
				{
					tag.Error = $"Unknown device '{tc.Device}'";
				}
				else
				{
					var parsed = dc.Driver.Parse(dc.Config, tc.Address ?? "", tc.DataType);

					if (parsed.Address == null)
					{
						tag.Error = parsed.Error;
					}
					else
					{
						if (!deviceWorkers.TryGetValue(dc.Config.Name, out var worker))
						{
							worker = engine.AcquireWorker(dc.Driver, dc.Config);
							deviceWorkers[dc.Config.Name] = worker;
							heldWorkers.Add(worker);
						}

						tag.Worker = worker;
						tag.Point = worker.Acquire(parsed.Address);
						tag.Point.AddListener(this);

						if (!byPoint.TryGetValue(tag.Point, out var list))
						{
							byPoint[tag.Point] = list = new List<SessionTag>();
						}

						list.Add(tag);
					}
				}

				tags.Add(tag);
				byId.TryAdd(tc.Id, tag);
				result.Add(new ConfiguredTag(tc.Id, tag.Handle, tag.Point?.Address.Normalized, tag.Error));
			}

			Release(old.Tags, old.Workers);

			return result;
		}
	}

	private void ReleaseAll()
	{
		var oldTags = tags;
		var oldWorkers = heldWorkers.ToList();
		tags = new List<SessionTag>();
		byId = new Dictionary<string, SessionTag>(StringComparer.Ordinal);
		byPoint = new Dictionary<Point, List<SessionTag>>();
		deviceWorkers = new Dictionary<string, DeviceWorker>(StringComparer.OrdinalIgnoreCase);
		heldWorkers.Clear();
		subscribed.Clear();
		pending.Clear();
		Release(oldTags, oldWorkers);
	}

	/// <summary>Releases tags that are no longer current. A point the current tags still use
	/// keeps this session as a listener.</summary>
	private void Release(List<SessionTag> oldTags, List<DeviceWorker> oldWorkers)
	{
		foreach (var tag in oldTags)
		{
			if (tag.Point != null && tag.Worker != null)
			{
				if (!byPoint.ContainsKey(tag.Point))
				{
					tag.Worker.Subscribe(tag.Point, this, 0);
					tag.Point.RemoveListener(this);
				}

				tag.Worker.Release(tag.Point);
			}
		}

		foreach (var w in oldWorkers)
		{
			engine.ReleaseWorker(w);
		}
	}

	public int? HandleOf(string id)
	{
		lock (sync)
		{
			return byId.TryGetValue(id, out var t) ? t.Handle : null;
		}
	}

	public string? IdOf(int handle)
	{
		lock (sync)
		{
			return Tag(handle)?.Config.Id;
		}
	}

	private SessionTag? Tag(int handle)
	{
		return handle >= 1 && handle <= tags.Count ? tags[handle - 1] : null;
	}

	// ------------------------------------------------------------ subscribe

	/// <summary>
	/// Replaces the subscription. Returns the snapshot of every requested tag,
	/// which the caller must send before any change. Tags are polled at rate,
	/// or at their entry in rates when there is one -- so each device can keep
	/// its own scan rate within one subscription.
	/// </summary>
	public IReadOnlyList<(int Handle, TagValue Value)> Subscribe(IReadOnlyList<int> handles, int rate,
		IReadOnlyDictionary<int, int>? rates = null)
	{
		lock (sync)
		{
			var wanted = new HashSet<int>(handles.Where(h => Tag(h) != null));

			foreach (int h in subscribed)
			{
				if (!wanted.Contains(h))
				{
					var t = Tag(h)!;
					UpdateSubscription(t, wanted, 0);
				}
			}

			subscribed.Clear();
			tagRates.Clear();
			rateMs = Math.Clamp(rate, 10, 3_600_000);

			if (rates != null)
			{
				foreach (var (h, r) in rates)
				{
					if (wanted.Contains(h))
					{
						tagRates[h] = Math.Clamp(r, 10, 3_600_000);
					}
				}
			}

			foreach (int h in wanted)
			{
				subscribed.Add(h);
			}

			foreach (int h in wanted)
			{
				UpdateSubscription(Tag(h)!, wanted, rateMs);
			}

			pending.Clear();

			return SnapshotLocked(wanted.OrderBy(h => h).ToList());
		}
	}

	public void Unsubscribe()
	{
		Subscribe(Array.Empty<int>(), rateMs);
	}

	/// <summary>A point stays subscribed for this session while any of its tags is.</summary>
	private void UpdateSubscription(SessionTag tag, HashSet<int> wanted, int rate)
	{
		if (tag.Point == null || tag.Worker == null)
		{
			return;
		}

		// A point read for several of this session's tags goes at the fastest of them.
		int fastest = 0;

		foreach (var t in byPoint[tag.Point])
		{
			if (wanted.Contains(t.Handle))
			{
				int r = rate > 0 && tagRates.TryGetValue(t.Handle, out var own) ? own : rate;
				fastest = fastest == 0 ? r : Math.Min(fastest, r);
			}
		}

		tag.Worker.Subscribe(tag.Point, this, fastest);
	}

	public IReadOnlyList<(int Handle, TagValue Value)> Snapshot(IReadOnlyList<int> handles)
	{
		lock (sync)
		{
			return SnapshotLocked(handles);
		}
	}

	private List<(int, TagValue)> SnapshotLocked(IReadOnlyList<int> handles)
	{
		var res = new List<(int, TagValue)>(handles.Count);

		foreach (int h in handles)
		{
			var t = Tag(h);

			if (t != null)
			{
				var v = ClientValue(t);
				t.LastSent = v;
				pending.Remove(h);
				res.Add((h, v));
			}
		}

		return res;
	}

	private static TagValue ClientValue(SessionTag t)
	{
		if (t.Point == null)
		{
			return TagValue.Bad(Status.Config, t.Error, 0);
		}

		var raw = t.Point.Value;

		return raw.IsGood ? raw with { Value = ValueCodec.ToClient(raw.Value, t.Config.Scale) } : raw;
	}

	// ------------------------------------------------------------ changes

	public void OnPointChanged(Point point)
	{
		bool any = false;

		lock (sync)
		{
			if (disposed || !byPoint.TryGetValue(point, out var list))
			{
				return;
			}

			foreach (var t in list)
			{
				if (!subscribed.Contains(t.Handle))
				{
					continue;
				}

				var v = ClientValue(t);

				if (t.LastSent is TagValue last && WithinDeadband(last, v, t.Config.Deadband))
				{
					continue;
				}

				t.LastSent = v;
				pending[t.Handle] = v;
				any = true;
			}
		}

		if (any)
		{
			ChangesAvailable?.Invoke();
		}
	}

	private static bool WithinDeadband(TagValue last, TagValue now, double deadband)
	{
		if (last.Quality != now.Quality || last.Status != now.Status || last.Error != now.Error)
		{
			return false;
		}

		if (deadband > 0 && ValueCodec.TryToDouble(last.Value, out double a) &&
			ValueCodec.TryToDouble(now.Value, out double b) && last.Value is not bool)
		{
			return Math.Abs(a - b) < deadband;
		}

		return Equals(last.Value, now.Value);
	}

	/// <summary>Everything changed since the last drain, latest value per tag.</summary>
	public List<(int Handle, TagValue Value)> DrainChanges()
	{
		lock (sync)
		{
			var res = pending.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key).ToList();
			pending.Clear();

			return res;
		}
	}

	// ------------------------------------------------------------ read / write

	/// <summary>Reads now, bypassing schedules and cache.</summary>
	public async Task<IReadOnlyList<(int Handle, TagValue Value)>> ReadAsync(IReadOnlyList<int> handles,
		TimeSpan timeout)
	{
		List<Task> waits;

		lock (sync)
		{
			waits = handles.Select(Tag).Where(t => t?.Point != null && t.Worker != null)
				.GroupBy(t => t!.Worker!)
				.Select(g => g.Key.ReadNowAsync(g.Select(t => t!.Point!)))
				.ToList();
		}

		await Task.WhenAny(Task.WhenAll(waits), Task.Delay(timeout));

		return Snapshot(handles);
	}

	/// <summary>
	/// Writes client values: inverse-scaled, coerced to each point's type, then
	/// sent to the devices. Each result is known only once the device answers.
	/// </summary>
	public async Task<IReadOnlyDictionary<int, WriteOutcome>> WriteAsync(
		IReadOnlyList<(int Handle, object? Value)> values, TimeSpan timeout)
	{
		var results = new Dictionary<int, WriteOutcome>();
		var byWorker = new Dictionary<DeviceWorker, List<(int Handle, WriteItem Item)>>();

		lock (sync)
		{
			foreach (var (h, value) in values)
			{
				var t = Tag(h);

				if (t == null)
				{
					results[h] = WriteOutcome.Fail("Unknown tag");

					continue;
				}

				if (t.Point == null || t.Worker == null)
				{
					results[h] = WriteOutcome.Fail(t.Error ?? "Tag is not configured");

					continue;
				}

				if (t.Config.ReadOnly || !t.Point.Address.Writable)
				{
					results[h] = WriteOutcome.Fail("Tag is read-only");

					continue;
				}

				var type = t.Point.Address.Type;
				object? raw = type == DataType.Bool ? value : ValueCodec.FromClient(value, t.Config.Scale);

				if (!ValueCodec.TryCoerce(raw, type, t.Point.Address.MaxLength, out var coerced, out var err))
				{
					results[h] = WriteOutcome.Fail(err!);

					continue;
				}

				if (!byWorker.TryGetValue(t.Worker, out var list))
				{
					byWorker[t.Worker] = list = new List<(int, WriteItem)>();
				}

				list.Add((h, new WriteItem(t.Point, coerced)));
			}
		}

		var pendingWrites = byWorker.Select(async kv =>
		{
			var task = kv.Key.WriteAsync(kv.Value.Select(x => x.Item).ToList());

			if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
			{
				return kv.Value.Select(x => (x.Handle, WriteOutcome.Fail("Timed out"))).ToList();
			}

			var outcomes = await task;

			return kv.Value.Select((x, i) => (x.Handle, i < outcomes.Length ? outcomes[i] :
				WriteOutcome.Fail("No result"))).ToList();
		}).ToList();

		foreach (var group in await Task.WhenAll(pendingWrites))
		{
			foreach (var (h, outcome) in group)
			{
				results[h] = outcome;
			}
		}

		return results;
	}

	// ------------------------------------------------------------ status

	public IReadOnlyList<DeviceStatus> Statuses()
	{
		lock (sync)
		{
			return deviceWorkers.Select(kv => StatusOf(kv.Key, kv.Value)).ToList();
		}
	}

	private static DeviceStatus StatusOf(string name, DeviceWorker w) =>
		new(name, w.State, w.LastError, Math.Round(w.PollMsAvg, 1), w.BadPoints);

	private void OnDeviceStateChanged(DeviceWorker worker)
	{
		List<DeviceStatus> mine;

		lock (sync)
		{
			if (disposed)
			{
				return;
			}

			mine = deviceWorkers.Where(kv => kv.Value == worker)
				.Select(kv => StatusOf(kv.Key, worker)).ToList();
		}

		if (mine.Count > 0)
		{
			StatusChanged?.Invoke(mine);
		}
	}

	/// <summary>The workers behind this session's devices, by session device name.</summary>
	public IReadOnlyList<(string Name, DeviceWorker Worker)> Devices()
	{
		lock (sync)
		{
			return deviceWorkers.Select(kv => (kv.Key, kv.Value)).ToList();
		}
	}

	public void Dispose()
	{
		engine.DeviceStateChanged -= OnDeviceStateChanged;

		lock (sync)
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			ReleaseAll();
		}
	}
}
