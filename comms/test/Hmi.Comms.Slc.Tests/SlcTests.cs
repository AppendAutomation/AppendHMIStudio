using System.Text;
using CSComm3.SLC.Tests.Integration;
using Hmi.Comms.Core;
using Hmi.Comms.Slc;

namespace Hmi.Comms.Slc.Tests;

public sealed class AddressTests
{
	private static SlcAddress A(string s) => (SlcAddress)SlcAddress.Parse(s).Address!;

	[Theory]
	[InlineData("N7:0", "N7:0", 0x89, 7, 0, 0, -1, DataType.Int16)]
	[InlineData("n7:12/3", "N7:12/3", 0x89, 7, 12, 0, 3, DataType.Bool)]
	[InlineData("B3:2/5", "B3:2/5", 0x85, 3, 2, 0, 5, DataType.Bool)]
	[InlineData("B3/37", "B3:2/5", 0x85, 3, 2, 0, 5, DataType.Bool)]
	[InlineData("F8:1", "F8:1", 0x8A, 8, 1, 0, -1, DataType.Float32)]
	[InlineData("L9:4", "L9:4", 0x91, 9, 4, 0, -1, DataType.Int32)]
	[InlineData("L9:4/20", "L9:4/20", 0x91, 9, 4, 1, 4, DataType.Bool)]
	[InlineData("S:1/5", "S:1/5", 0x84, 2, 1, 0, 5, DataType.Bool)]
	[InlineData("S2:1", "S:1", 0x84, 2, 1, 0, -1, DataType.Int16)]
	[InlineData("ST9:0", "ST9:0", 0x8D, 9, 0, 0, -1, DataType.String)]
	[InlineData("A10:3", "A10:3", 0x8E, 10, 3, 0, -1, DataType.String)]
	[InlineData("T4:0.ACC", "T4:0.ACC", 0x86, 4, 0, 2, -1, DataType.Int16)]
	[InlineData("T4:1.PRE", "T4:1.PRE", 0x86, 4, 1, 1, -1, DataType.Int16)]
	[InlineData("T4:1.DN", "T4:1.DN", 0x86, 4, 1, 0, 13, DataType.Bool)]
	[InlineData("C5:0.CU", "C5:0.CU", 0x87, 5, 0, 0, 15, DataType.Bool)]
	[InlineData("R6:2.POS", "R6:2.POS", 0x88, 6, 2, 2, -1, DataType.Int16)]
	[InlineData("I:1.0/3", "I:1.0/3", 0x83, 1, 1, 0, 3, DataType.Bool)]
	[InlineData("O:2", "O:2.0", 0x82, 0, 2, 0, -1, DataType.Int16)]
	[InlineData("N7:255", "N7:255", 0x89, 7, 255, 0, -1, DataType.Int16)]
	public void Valid(string text, string normalized, int type, int file, int element, int word, int bit, DataType dt)
	{
		var a = A(text);

		Assert.Equal(normalized, a.Normalized);
		Assert.Equal((byte)type, a.FileType);
		Assert.Equal(file, a.FileNumber);
		Assert.Equal(element, a.Element);
		Assert.Equal(word, a.Word);
		Assert.Equal(bit, a.Bit);
		Assert.Equal(dt, a.Type);
	}

	[Theory]
	[InlineData("N:0", "needs a file number")]
	[InlineData("N7:0/16", "Bit must be 0 to 15")]
	[InlineData("L9:0/32", "Bit must be 0 to 31")]
	[InlineData("F8:0/1", "no bits")]
	[InlineData("T4:0", "Name a member")]
	[InlineData("T4:0.XYZ", "has no member XYZ")]
	[InlineData("C5:0.TT", "has no member TT")]
	[InlineData("Q9:1", "Not an SLC address")]
	[InlineData("40001", "Not an SLC address")]
	public void Invalid(string text, string message)
	{
		Assert.Contains(message, SlcAddress.Parse(text).Error);
	}

	[Fact]
	public void InputsAreReadOnly()
	{
		Assert.False(A("I:1.0").Writable);
		Assert.True(A("O:1.0").Writable);
		Assert.True(A("N7:0").Writable);
	}
}

public sealed class BlockBuilderTests
{
	private static List<Point> Points(params string[] addrs)
	{
		var device = new DeviceConfig { Name = "t", Protocol = "slc", Host = "x" };
		var worker = new DeviceWorker("t", new SlcDriver(), device);

		return addrs.Select(a => worker.Acquire(SlcAddress.Parse(a).Address!)).ToList();
	}

	private static string D(List<SlcBlock> b) => string.Join(" | ",
		b.Select(x => x.IO ? $"io{x.StartElement}.{x.Word}" : $"{x.File}:{x.StartElement}+{x.Elements}"));

	[Fact]
	public void IntegersMergeIntoOneRead()
	{
		Assert.Equal("7:0+40", D(SlcBlockBuilder.Build(Points("N7:0", "N7:5/2", "N7:39"),
			new SlcLimits(MaxGapElements: 40))));
	}

	[Fact]
	public void ByteBudgetCapsTheRun()
	{
		// 236 bytes: 118 integers, 59 floats, 39 timers, 2 strings.
		Assert.Equal("7:0+118 | 7:118+1", D(SlcBlockBuilder.Build(Points("N7:0", "N7:117", "N7:118"),
			new SlcLimits(MaxGapElements: 200))));
		Assert.Equal("8:0+59 | 8:59+1", D(SlcBlockBuilder.Build(Points("F8:0", "F8:58", "F8:59"),
			new SlcLimits(MaxGapElements: 200))));
		Assert.Equal("4:0+39 | 4:39+1", D(SlcBlockBuilder.Build(Points("T4:0.ACC", "T4:38.DN", "T4:39.PRE"),
			new SlcLimits(MaxGapElements: 200))));
		Assert.Equal("9:0+2 | 9:2+1", D(SlcBlockBuilder.Build(Points("ST9:0", "ST9:1", "ST9:2"),
			new SlcLimits(MaxGapElements: 200))));
	}

	[Fact]
	public void GapLimitSplits()
	{
		Assert.Equal("7:0+1 | 7:10+1", D(SlcBlockBuilder.Build(Points("N7:0", "N7:10"), new SlcLimits(MaxGapElements: 8))));
		Assert.Equal("7:0+10", D(SlcBlockBuilder.Build(Points("N7:0", "N7:9"), new SlcLimits(MaxGapElements: 8))));
	}

	[Fact]
	public void FilesAndIoStaySeparate()
	{
		var b = SlcBlockBuilder.Build(Points("N7:0", "N10:0", "B3:0/1", "I:1.0", "I:1.1/4"), new SlcLimits());

		Assert.Equal("io1.0 | io1.1 | 3:0+1 | 7:0+1 | 10:0+1", D(b));
	}
}

public sealed class SimulatorIntegrationTests : IDisposable
{
	private const byte N = 0x89, F = 0x8A, B = 0x85, T = 0x86, L = 0x91, ST = 0x8D, I = 0x83;
	private readonly SlcSimulator sim = new();
	private readonly CommsEngine engine = new(new IProtocolDriver[] { new SlcDriver() });

	public SimulatorIntegrationTests()
	{
		sim.AddFile(N, 7, 2 * 50);
		sim.AddFile(F, 8, 4 * 10);
		sim.AddFile(B, 3, 2 * 4);
		sim.AddFile(T, 4, 6 * 4);
		sim.AddFile(L, 9, 4 * 2);
		sim.AddFile(ST, 10, 84 * 2);
		sim.AddFile(I, 1, 2 * 8);

		for (int i = 0; i < 50; i++)
		{
			BitConverter.GetBytes((short)(i * 3)).CopyTo(sim.File(N, 7), i * 2);
		}

		BitConverter.GetBytes(0.1f).CopyTo(sim.File(F, 8), 4);
		sim.File(B, 3)[2] = 0b0010_0000;
		BitConverter.GetBytes((short)(1 << 13)).CopyTo(sim.File(T, 4), 6);
		BitConverter.GetBytes((short)500).CopyTo(sim.File(T, 4), 6 + 2);
		BitConverter.GetBytes((short)123).CopyTo(sim.File(T, 4), 6 + 4);
		BitConverter.GetBytes(1 << 20).CopyTo(sim.File(L, 9), 0);
		BitConverter.GetBytes((short)5).CopyTo(sim.File(ST, 10), 0);
		// As a processor stores it: each word's bytes swapped, "HELLO" as "EHLL\0O".
		new byte[] { (byte)'E', (byte)'H', (byte)'L', (byte)'L', 0, (byte)'O' }.CopyTo(sim.File(ST, 10), 2);
		// The simulator places I/O words at slot * 2 + word * 2: I:1.2 is byte 6.
		BitConverter.GetBytes((short)0x0008).CopyTo(sim.File(I, 1), 6);
	}

	public void Dispose()
	{
		engine.Dispose();
		sim.Dispose();
	}

	private DeviceConfig Device() => new()
	{
		Name = "Old",
		Protocol = "slc",
		Host = "127.0.0.1",
		Port = sim.Port,
		TimeoutMs = 1000
	};

	private static TagConfig Tag(string id, string address) => new() { Id = id, Device = "Old", Address = address };

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
	public async Task ReadsEveryKindOfAddress()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("n0", "N7:0"), Tag("n10", "N7:10"), Tag("n12bit", "N7:12/2"), Tag("f", "F8:1"),
			Tag("bit", "B3:1/5"), Tag("dn", "T4:1.DN"), Tag("pre", "T4:1.PRE"), Tag("acc", "T4:1.ACC"),
			Tag("lbit", "L9:0/20"), Tag("str", "ST10:0"), Tag("in", "I:1.2/3")
		});
		Assert.All(c, x => Assert.Null(x.Error));
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 11 && d.Values.All(x => x.IsGood));

		Assert.Equal(0L, v[1].Value);
		Assert.Equal(30L, v[2].Value);
		Assert.Equal(true, v[3].Value);     // 36 = 0b100100
		Assert.Equal(0.1, v[4].Value);
		Assert.Equal(true, v[5].Value);
		Assert.Equal(true, v[6].Value);
		Assert.Equal(500L, v[7].Value);
		Assert.Equal(123L, v[8].Value);
		Assert.Equal(true, v[9].Value);
		Assert.Equal("HELLO", v[10].Value);
		Assert.Equal(true, v[11].Value);
	}

	[Fact]
	public async Task NeighbouringIntegersCostOneRequest()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, Enumerable.Range(0, 40).Select(i => Tag("n" + i, $"N7:{i}")).ToList());
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);
		await Until(s, d => d.Count == 40 && d.Values.All(x => x.IsGood));
		await Task.Delay(200);

		var w = engine.Workers.Single();
		Assert.Equal(1, w.RequestsPerCycle);
		Assert.Contains("elements 0..39", w.DescribePlan()[0]);
	}

	[Fact]
	public async Task ARunPastTheEndOfTheFileSplitsAndOnlyTheMissingElementIsBad()
	{
		var s = engine.CreateSession();

		// N7 has 50 elements: N7:52 does not exist, and merging it in makes the
		// whole run fail until it is split out.
		var c = s.Configure(new[] { Device() }, new[] { Tag("a", "N7:45"), Tag("b", "N7:49"), Tag("x", "N7:52") });
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 3 && d[1].IsGood && d[2].IsGood && d[3].Status == Status.Device);

		Assert.Equal(135L, v[1].Value);
		Assert.Equal(147L, v[2].Value);
		Assert.Equal(DeviceState.Connected, engine.Workers.Single().State);

		// The good elements are read together again, the missing one alone.
		await Task.Delay(300);
		var plan = engine.Workers.Single().DescribePlan();
		Assert.Contains(plan, p => p.Contains("elements 45..49"));
	}

	[Fact]
	public async Task Writes()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("n", "N7:3"), Tag("f", "F8:2"), Tag("bit", "B3:0/4"), Tag("pre", "T4:2.PRE"), Tag("str", "ST10:1"),
			Tag("in", "I:1.0"), Tag("lbit", "L9:1/17")
		});
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);
		await Until(s, d => d.Count == 7 && d.Values.All(x => x.IsGood));

		var r = await s.WriteAsync(new (int, object?)[]
		{
			(1, -42.0), (2, 6.25), (3, true), (4, 900.0), (5, "Mixer"), (6, 1.0), (7, true)
		}, TimeSpan.FromSeconds(5));

		Assert.True(r[1].Ok, r[1].Error);
		Assert.True(r[2].Ok, r[2].Error);
		Assert.True(r[3].Ok, r[3].Error);
		Assert.True(r[4].Ok, r[4].Error);
		Assert.True(r[5].Ok, r[5].Error);
		Assert.Equal("Tag is read-only", r[6].Error);
		Assert.True(r[7].Ok, r[7].Error);

		Assert.Equal(-42, BitConverter.ToInt16(sim.File(N, 7), 6));
		Assert.Equal(6.25f, BitConverter.ToSingle(sim.File(F, 8), 8));
		Assert.Equal(0b0001_0000, sim.File(B, 3)[0]);

		// And clearing, which a mask/value mix-up once broke.
		var clear = await s.WriteAsync(new (int, object?)[] { (3, false) }, TimeSpan.FromSeconds(5));
		Assert.True(clear[3].Ok, clear[3].Error);
		Assert.Equal(0, sim.File(B, 3)[0]);
		Assert.Equal(900, BitConverter.ToInt16(sim.File(T, 4), 12 + 2));
		Assert.Equal(5, BitConverter.ToInt16(sim.File(ST, 10), 84));
		Assert.Equal("iMex\0r", Encoding.ASCII.GetString(sim.File(ST, 10), 86, 6));
		Assert.Equal(1 << 17, BitConverter.ToInt32(sim.File(L, 9), 4));

		v = await Until(s, d => Equals(d[5].Value, "Mixer") && Equals(d[1].Value, -42L));
	}

	private Dictionary<int, TagValue> v = new();

	[Fact]
	public async Task LosingTheProcessorIsCommAndReconnects()
	{
		var s = engine.CreateSession();
		s.Configure(new[] { Device() }, new[] { Tag("a", "N7:1") });
		s.Subscribe(new[] { 1 }, 50);
		var got = await Until(s, d => d.TryGetValue(1, out var x) && x.IsGood);

		sim.DropConnections();
		BitConverter.GetBytes((short)77).CopyTo(sim.File(N, 7), 2);

		got = await Until(s, d => d[1].IsGood && Equals(d[1].Value, 77L), got);
		Assert.True(engine.Workers.Single().Reconnects >= 1);
	}

	[Fact]
	public async Task NoProcessorIsComm()
	{
		sim.Dispose();
		var s = engine.CreateSession();
		s.Configure(new[] { Device() }, new[] { Tag("a", "N7:1") });
		s.Subscribe(new[] { 1 }, 50);

		await Until(s, d => d.TryGetValue(1, out var x) && x.Status == Status.Comm);
		Assert.Equal(DeviceState.Backoff, engine.Workers.Single().State);
	}

	[Fact]
	public async Task UnswappedStringsForADeviceThatStoresThemPlain()
	{
		Encoding.ASCII.GetBytes("HELLO").CopyTo(sim.File(ST, 10), 2);
		var dev = Device() with { Options = new Dictionary<string, string> { ["swapStringBytes"] = "false" } };
		var s = engine.CreateSession();
		s.Configure(new[] { dev }, new[] { Tag("str", "ST10:0") });
		s.Subscribe(new[] { 1 }, 50);

		var got = await Until(s, d => d.TryGetValue(1, out var x) && x.IsGood);
		Assert.Equal("HELLO", got[1].Value);
	}
}
