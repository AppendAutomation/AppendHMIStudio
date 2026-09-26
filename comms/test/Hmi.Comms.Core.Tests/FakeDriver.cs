using Hmi.Comms.Core;

namespace Hmi.Comms.Core.Tests;

/// <summary>
/// A protocol whose "device" is a dictionary. Addresses are any name, with an
/// optional ":TYPE" suffix; "RO:" makes a point read-only; "BAD:" makes the
/// device refuse it.
/// </summary>
internal sealed class FakeDriver : IProtocolDriver
{
	public readonly Dictionary<string, object?> Values = new(StringComparer.OrdinalIgnoreCase);
	public readonly List<string> Log = new();
	public int Connects;
	public bool FailConnect;
	public bool FailRead;
	public List<FakeConnection> Connections = new();

	public string Protocol => "fake";
	public int DefaultPort => 1;

	public string? ValidateDevice(DeviceConfig device) =>
		string.IsNullOrEmpty(device.Host) ? "Host is required" : null;

	public string ConnectionKey(DeviceConfig device) => device.Host;

	public ParseResult Parse(DeviceConfig device, string address, string? hint)
	{
		if (string.IsNullOrWhiteSpace(address) || address.Contains(' '))
		{
			return ParseResult.Fail("Bad address");
		}

		var parts = address.Split(':');
		string name = parts[^1];
		bool ro = parts.Contains("RO");
		bool bad = parts.Contains("BAD");
		var type = DataTypes.ParseHint(hint) ?? DataType.Float64;

		if (parts.Length > 1 && DataTypes.ParseHint(parts[0]) is DataType t)
		{
			type = t;
		}

		return ParseResult.Ok(new FakeAddress(name.ToUpperInvariant(), type, ro, bad));
	}

	public IDeviceConnection Create(DeviceConfig device)
	{
		var c = new FakeConnection(this);
		Connections.Add(c);

		return c;
	}
}

internal sealed class FakeAddress : ParsedAddress
{
	private readonly string name;
	private readonly DataType type;
	private readonly bool ro;

	public FakeAddress(string name, DataType type, bool ro, bool bad)
	{
		this.name = name;
		this.type = type;
		this.ro = ro;
		Bad = bad;
	}

	public bool Bad { get; }
	public string Name => name;
	public override string Key => name;
	public override string Normalized => name;
	public override DataType Type => type;
	public override bool Writable => !ro;
	public override int? MaxLength => type == DataType.String ? 10 : null;
}

internal sealed class FakePlan : IReadPlan
{
	public required List<Point> Points;
	public int RequestCount => 1;
	public IReadOnlyList<string> Describe() => new[] { string.Join(",", Points.Select(p => p.Address.Key)) };
}

internal sealed class FakeConnection : IDeviceConnection
{
	private readonly FakeDriver driver;
	public bool Disposed;
	public readonly List<List<string>> Reads = new();
	public readonly List<List<(string, object?)>> Writes = new();
	public int PlansBuilt;

	public FakeConnection(FakeDriver driver)
	{
		this.driver = driver;
	}

	public void Connect()
	{
		driver.Connects++;

		if (driver.FailConnect)
		{
			throw new IOException("connection refused");
		}
	}

	public IReadPlan Plan(IReadOnlyList<Point> points)
	{
		PlansBuilt++;

		return new FakePlan { Points = points.ToList() };
	}

	public void Read(IReadPlan plan, IReadSink sink)
	{
		if (driver.FailRead)
		{
			throw new IOException("socket closed");
		}

		var p = (FakePlan)plan;
		Reads.Add(p.Points.Select(x => x.Address.Key).ToList());

		foreach (var point in p.Points)
		{
			var a = (FakeAddress)point.Address;

			if (a.Bad)
			{
				sink.Fail(point, Status.Device, "Illegal address");
			}
			else
			{
				driver.Values.TryGetValue(a.Name, out var v);
				sink.Set(point, v);
			}
		}
	}

	public WriteOutcome[] Write(IReadOnlyList<WriteItem> items)
	{
		Writes.Add(items.Select(i => (i.Point.Address.Key, i.Raw)).ToList());

		return items.Select(i =>
		{
			var a = (FakeAddress)i.Point.Address;

			if (a.Bad)
			{
				return WriteOutcome.Fail("Refused");
			}

			driver.Values[a.Name] = i.Raw;

			return WriteOutcome.Success;
		}).ToArray();
	}

	public void Dispose()
	{
		Disposed = true;
	}
}

/// <summary>An engine on a manual clock whose workers the test steps.</summary>
internal sealed class Rig : IDisposable
{
	public long Now = 1000;
	public long Wall = 1_700_000_000_000;
	public readonly FakeDriver Driver = new();
	public readonly CommsEngine Engine;

	public Rig()
	{
		Engine = new CommsEngine(new IProtocolDriver[] { Driver })
		{
			Clock = () => Now,
			WallClock = () => Wall,
			StartWorkers = false,
			Jitter = false
		};
	}

	public static DeviceConfig Device(string name = "D1", string host = "h1", int minScan = 50) => new()
	{
		Name = name,
		Protocol = "fake",
		Host = host,
		MinScanMs = minScan
	};

	public static TagConfig Tag(string id, string address, string device = "D1", Scaling? scale = null,
		double deadband = 0, bool readOnly = false, string? type = null) => new()
	{
		Id = id,
		Device = device,
		Address = address,
		Scale = scale,
		Deadband = deadband,
		ReadOnly = readOnly,
		DataType = type
	};

	public DeviceWorker Worker => Engine.Workers.Single();

	/// <summary>Advances the clock and steps every worker once.</summary>
	public void Step(long advanceMs = 0)
	{
		Now += advanceMs;
		Wall += advanceMs;

		foreach (var w in Engine.Workers)
		{
			w.Step();
		}
	}

	public void Dispose() => Engine.Dispose();
}
