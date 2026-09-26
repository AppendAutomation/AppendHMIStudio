using CSLogix.Tests.Integration;
using Hmi.Comms.Core;
using Hmi.Comms.Logix;

namespace Hmi.Comms.Logix.Tests;

public sealed class AddressTests
{
	[Theory]
	[InlineData("Tank_Level", "TANK_LEVEL", -1)]
	[InlineData("  Pump.Run  ", "PUMP.RUN", -1)]
	[InlineData("Program:Main.Count", "PROGRAM:MAIN.COUNT", -1)]
	[InlineData("Arr[3]", "ARR[3]", -1)]
	[InlineData("Grid[1,2,3].Value", "GRID[1,2,3].VALUE", -1)]
	[InlineData("Status.5", "STATUS", 5)]
	[InlineData("Motor[2].Faults.31", "MOTOR[2].FAULTS", 31)]
	public void Valid(string text, string readKey, int bit)
	{
		var a = (LogixAddress)LogixAddress.Parse(text).Address!;

		Assert.Equal(readKey, a.ReadKey);
		Assert.Equal(bit, a.Bit);
		Assert.Equal(bit >= 0 ? DataType.Bool : DataType.Unknown, a.Type);
		Assert.Equal(text.Trim(), a.Normalized);
	}

	[Theory]
	[InlineData("")]
	[InlineData("1Tank")]
	[InlineData("Tank Level")]
	[InlineData("Arr[]")]
	[InlineData("Arr[1,2,3,4]")]
	[InlineData("Tag..Member")]
	[InlineData("Program:.Tag")]
	[InlineData("Status.64")]
	public void Invalid(string text)
	{
		Assert.NotNull(LogixAddress.Parse(text).Error);
	}

	[Fact]
	public void CaseDoesNotMakeADifferentPoint()
	{
		Assert.Equal(LogixAddress.Parse("Speed").Address!.Key, LogixAddress.Parse("SPEED").Address!.Key);
	}

	[Fact]
	public void PathBytesFollowTheWireEncoding()
	{
		// 0x91, len, "Speed", pad = 8
		Assert.Equal(8, ((LogixAddress)LogixAddress.Parse("Speed").Address!).PathBytes);

		// "Arr" 6 + 8-bit element 2 = 8; 16-bit element 4
		Assert.Equal(8, ((LogixAddress)LogixAddress.Parse("Arr[3]").Address!).PathBytes);
		Assert.Equal(10, ((LogixAddress)LogixAddress.Parse("Arr[300]").Address!).PathBytes);
	}
}

public sealed class ChunkerTests
{
	private static List<LogixRead> Reads(int n, string prefix = "Tag") =>
		Enumerable.Range(0, n).Select(i => new LogixRead
		{
			Tag = prefix + i,
			Key = (prefix + i).ToUpperInvariant(),
			PathBytes = ((LogixAddress)LogixAddress.Parse(prefix + i).Address!).PathBytes
		}).ToList();

	[Fact]
	public void UnknownSizesAssumeAString()
	{
		var chunks = LogixChunker.Chunk(Reads(20), 504, _ => null);

		// Each reply budgeted at 96 bytes: five fit in 504.
		Assert.All(chunks, c => Assert.InRange(c.Count, 1, 5));
		Assert.Equal(20, chunks.Sum(c => c.Count));
	}

	[Fact]
	public void LearnedSizesPackTighter()
	{
		var reads = Reads(300);
		var small = LogixChunker.Chunk(reads, 504, _ => 4);
		var large = LogixChunker.Chunk(reads, 4002, _ => 4);

		Assert.True(small.Count > large.Count);
		Assert.True(large.Count <= 2, $"{large.Count} chunks at 4002");

		foreach (var c in small)
		{
			int request = 8 + c.Sum(LogixChunker.RequestCost);
			int reply = 6 + c.Count * LogixChunker.ReplyCost(4);
			Assert.True(request <= 504 && reply <= 504, $"request {request} reply {reply}");
		}
	}

	[Fact]
	public void LongPathsLimitTheRequestSide()
	{
		var reads = Reads(50, "A_Rather_Long_Tag_Name_For_Testing_");
		var chunks = LogixChunker.Chunk(reads, 504, _ => 1);

		foreach (var c in chunks)
		{
			Assert.True(8 + c.Sum(LogixChunker.RequestCost) <= 504 - 16);
		}
	}
}

/// <summary>The engine with a real worker against the controller simulator.</summary>
public sealed class SimulatorIntegrationTests : IDisposable
{
	private readonly LogixSimulator sim = new();
	private readonly CommsEngine engine = new(new IProtocolDriver[] { new LogixDriver() });

	public SimulatorIntegrationTests()
	{
		sim.Set("Speed", LogixSimulator.DINT, 1500);
		sim.Set("Small", LogixSimulator.INT, (short)-7);
		sim.Set("Temp", LogixSimulator.REAL, 0.1f);
		sim.Set("Big", LogixSimulator.LREAL, 2.5);
		sim.Set("Name", LogixSimulator.STRUCT, "Line 1");
		sim.Set("Running", LogixSimulator.BOOL, true);
		sim.Set("Flags", LogixSimulator.DINT, 0b1010);
		sim.Set("Arr[3]", LogixSimulator.DINT, 33);
		sim.Set("Program:Main.Count", LogixSimulator.DINT, 9);
	}

	public void Dispose()
	{
		engine.Dispose();
		sim.Dispose();
	}

	private DeviceConfig Device() => new()
	{
		Name = "Line1",
		Protocol = "logix",
		Host = "127.0.0.1",
		Port = sim.Port,
		TimeoutMs = 1000
	};

	private static TagConfig Tag(string id, string address, Scaling? scale = null) => new()
	{
		Id = id,
		Device = "Line1",
		Address = address,
		Scale = scale
	};

	private static async Task<Dictionary<int, TagValue>> Until(EngineSession s,
		Func<Dictionary<int, TagValue>, bool> done, Dictionary<int, TagValue>? latest = null)
	{
		latest ??= new Dictionary<int, TagValue>();
		var deadline = DateTime.UtcNow.AddSeconds(10);

		while (DateTime.UtcNow < deadline)
		{
			foreach (var (h, v) in s.DrainChanges())
			{
				latest[h] = v;
			}

			if (done(latest))
			{
				return latest;
			}

			await Task.Delay(20);
		}

		throw new TimeoutException("have " + string.Join(", ",
			latest.Select(kv => $"{kv.Key}={kv.Value.Value}/{kv.Value.Quality}/{kv.Value.Status}/{kv.Value.Error}")));
	}

	[Fact]
	public async Task ReadsEveryTypeInOnePacket()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("speed", "Speed"), Tag("small", "Small"), Tag("temp", "Temp"), Tag("big", "Big"),
			Tag("name", "Name"), Tag("run", "Running"), Tag("arr", "Arr[3]"), Tag("prog", "Program:Main.Count"),
			Tag("scaled", "Speed", new Scaling(0, 3000, 0, 100))
		});
		Assert.All(c, x => Assert.Null(x.Error));
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 9 && d.Values.All(x => x.IsGood));

		Assert.Equal(1500L, v[1].Value);
		Assert.Equal(-7L, v[2].Value);
		Assert.Equal(0.1, v[3].Value);
		Assert.Equal(2.5, v[4].Value);
		Assert.Equal("Line 1", v[5].Value);
		Assert.Equal(true, v[6].Value);
		Assert.Equal(33L, v[7].Value);
		Assert.Equal(9L, v[8].Value);
		Assert.Equal(50.0, v[9].Value);

		// Eight distinct tags, one packet per poll.
		Assert.Equal(1, engine.Workers.Single().RequestsPerCycle);
	}

	[Fact]
	public async Task BitsShareOneReadOfTheInteger()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("b0", "Flags.0"), Tag("b1", "Flags.1"), Tag("b3", "Flags.3"), Tag("whole", "Flags")
		});
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 4 && d.Values.All(x => x.IsGood));

		Assert.Equal(false, v[1].Value);
		Assert.Equal(true, v[2].Value);
		Assert.Equal(true, v[3].Value);
		Assert.Equal(10L, v[4].Value);
		Assert.Single(engine.Workers.Single().DescribePlan());
		Assert.Contains("1 tags", engine.Workers.Single().DescribePlan()[0]);
	}

	[Fact]
	public async Task BoolArraysAreUnpackedFromTheirDwords()
	{
		// A BOOL[64] is two DWORDs; element numbers on it count DWORDs.
		sim.Set("Alarms[0]", LogixSimulator.DWORD, 0b1010u);
		sim.Set("Alarms[1]", LogixSimulator.DWORD, 1u << 5);

		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("a0", "Alarms[0]"), Tag("a1", "Alarms[1]"), Tag("a3", "Alarms[3]"), Tag("a37", "Alarms[37]"),
			Tag("a63", "Alarms[63]")
		});
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 5 && d.Values.All(x => x.IsGood && x.Value is bool));

		Assert.Equal(false, v[1].Value);
		Assert.Equal(true, v[2].Value);
		Assert.Equal(true, v[3].Value);
		Assert.Equal(true, v[4].Value);
		Assert.Equal(false, v[5].Value);

		var r = await s.WriteAsync(new (int, object?)[] { (4, false), (5, true) }, TimeSpan.FromSeconds(5));
		Assert.True(r[4].Ok, r[4].Error);
		Assert.True(r[5].Ok, r[5].Error);
		Assert.Equal(1u << 31, sim.Get("Alarms[1]"));
		Assert.Equal(0b1010u, sim.Get("Alarms[0]"));
	}

	[Fact]
	public async Task AMissingTagIsBadAloneAndTheRestReadOn()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[] { Tag("a", "Speed"), Tag("x", "Nope"), Tag("b", "Temp") });
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 3 && d[1].IsGood && d[3].IsGood && d[2].Status == Status.Device);

		Assert.Equal("Path segment error", v[2].Error);
		Assert.Equal(DeviceState.Connected, engine.Workers.Single().State);
	}

	[Fact]
	public async Task WritesUseTheControllersTypes()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("small", "Small"), Tag("temp", "Temp"), Tag("name", "Name"), Tag("bit", "Flags.0"),
			Tag("sp", "Speed", new Scaling(0, 3000, 0, 100))
		});
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);
		await Until(s, d => d.Count == 5 && d.Values.All(x => x.IsGood));

		var r = await s.WriteAsync(new (int, object?)[]
		{
			(1, 12.0), (2, 3.5), (3, "Mixer"), (4, true), (5, 25.0)
		}, TimeSpan.FromSeconds(5));

		Assert.All(r.Values, o => Assert.True(o.Ok, o.Error));
		Assert.Equal((short)12, sim.Get("Small"));
		Assert.Equal(3.5f, sim.Get("Temp"));
		Assert.Equal("Mixer", sim.Get("Name"));
		Assert.Equal(0b1011, sim.Get("Flags"));
		Assert.Equal(750, sim.Get("Speed"));
		Assert.Equal(1, sim.ReadModifyWriteCount);

		var overflow = await s.WriteAsync(new (int, object?)[] { (1, 40000.0) }, TimeSpan.FromSeconds(5));
		Assert.Contains("Out of range", overflow[1].Error);
	}

	[Fact]
	public async Task WritingBeforeAnyReadLearnsTheTypeFirst()
	{
		var s = engine.CreateSession();
		s.Configure(new[] { Device() }, new[] { Tag("small", "Small") });

		var r = await s.WriteAsync(new (int, object?)[] { (1, 99.0) }, TimeSpan.FromSeconds(5));

		Assert.True(r[1].Ok, r[1].Error);
		Assert.Equal((short)99, sim.Get("Small"));
	}

	[Fact]
	public async Task ManyStringsOnASmallConnectionAreSplitToFit()
	{
		sim.SupportLargeForwardOpen = false;

		for (int i = 0; i < 12; i++)
		{
			sim.Set("S" + i, LogixSimulator.STRUCT, "text " + i);
		}

		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, Enumerable.Range(0, 12).Select(i => Tag("s" + i, "S" + i)).ToList());
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 12 && d.Values.All(x => x.IsGood));

		Assert.Equal("text 11", v[12].Value);
		// Points subscribed a moment apart start out of phase; a few cycles on
		// they are read together.
		await Task.Delay(300);
		var w = engine.Workers.Single();
		Assert.True(w.RequestsPerCycle >= 3, $"{w.RequestsPerCycle} requests: " +
			string.Join(" | ", w.DescribePlan()) + $" size {sim.NegotiatedSize}");
	}

	[Fact]
	public async Task LosingTheControllerIsCommAndReconnects()
	{
		var s = engine.CreateSession();
		s.Configure(new[] { Device() }, new[] { Tag("a", "Speed") });
		s.Subscribe(new[] { 1 }, 50);
		var v = await Until(s, d => d.TryGetValue(1, out var x) && x.IsGood);

		sim.DropConnections();
		sim.Set("Speed", LogixSimulator.DINT, 1600);

		// Either the drop is seen (comm) and then recovery, or the reconnect is
		// quick enough to go straight to the new value; both end on 1600.
		v = await Until(s, d => d[1].IsGood && Equals(d[1].Value, 1600L), v);
		Assert.True(engine.Workers.Single().Reconnects >= 1);
	}

	[Fact]
	public async Task NoControllerIsComm()
	{
		sim.Dispose();
		var s = engine.CreateSession();
		s.Configure(new[] { Device() }, new[] { Tag("a", "Speed") });
		s.Subscribe(new[] { 1 }, 50);

		var v = await Until(s, d => d.TryGetValue(1, out var x) && x.Status == Status.Comm);
		Assert.Equal(DeviceState.Backoff, engine.Workers.Single().State);
	}
}
