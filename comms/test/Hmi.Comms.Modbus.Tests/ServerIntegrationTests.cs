using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FluentModbus;
using Hmi.Comms.Core;
using Hmi.Comms.Modbus;

namespace Hmi.Comms.Modbus.Tests;

/// <summary>A real FluentModbus server on loopback. Its buffers hold wire bytes.</summary>
internal sealed class TestModbusServer : IDisposable
{
	public readonly ModbusTcpServer Server = new();
	public readonly int Port;

	public TestModbusServer(int? port = null)
	{
		Port = port ?? FreePort();
		Server.Start(new IPEndPoint(IPAddress.Loopback, Port));
	}

	public static int FreePort()
	{
		var l = new TcpListener(IPAddress.Loopback, 0);
		l.Start();
		int p = ((IPEndPoint)l.LocalEndpoint).Port;
		l.Stop();

		return p;
	}

	public Span<byte> Registers => Server.GetHoldingRegisterBuffer<byte>();

	public void SetRegister(int offset, ushort value) =>
		BinaryPrimitives.WriteUInt16BigEndian(Registers.Slice(offset * 2), value);

	public ushort GetRegister(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Registers.Slice(offset * 2));

	public void SetFloat(int offset, float value) =>
		BinaryPrimitives.WriteSingleBigEndian(Registers.Slice(offset * 2), value);

	public void SetCoil(int n, bool on)
	{
		var buf = Server.GetCoilBuffer<byte>();

		if (on)
		{
			buf[n / 8] |= (byte)(1 << (n % 8));
		}
		else
		{
			buf[n / 8] &= (byte)~(1 << (n % 8));
		}
	}

	public bool GetCoil(int n) => (Server.GetCoilBuffer<byte>()[n / 8] & (1 << (n % 8))) != 0;

	public void Dispose()
	{
		Server.Stop();
		Server.Dispose();
	}
}

/// <summary>The engine with real worker threads against a real Modbus server.</summary>
public sealed class ServerIntegrationTests : IDisposable
{
	private readonly TestModbusServer plc = new();
	private readonly CommsEngine engine = new(new IProtocolDriver[] { new ModbusDriver() });

	public void Dispose()
	{
		engine.Dispose();
		plc.Dispose();
	}

	private DeviceConfig Device(string byteOrder = "BE") => new()
	{
		Name = "Pump",
		Protocol = "modbus",
		Host = "127.0.0.1",
		Port = plc.Port,
		TimeoutMs = 1000,
		Options = new Dictionary<string, string> { ["unitId"] = "0", ["byteOrder"] = byteOrder }
	};

	private static TagConfig Tag(string id, string address, Scaling? scale = null) => new()
	{
		Id = id,
		Device = "Pump",
		Address = address,
		Scale = scale
	};

	/// <summary>Collects changes until a condition over the latest values holds.</summary>
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

		throw new TimeoutException("condition not met; have " +
			string.Join(", ", latest.Select(kv => $"{kv.Key}={kv.Value.Value}/{kv.Value.Quality}/{kv.Value.Status}")));
	}

	[Fact]
	public async Task ReadsEveryTypeAndFollowsChanges()
	{
		plc.SetRegister(0, 1234);
		plc.SetRegister(1, 0xFFFF);
		plc.SetFloat(2, 12.5f);
		plc.SetRegister(4, 0x0008);
		plc.SetCoil(3, true);

		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("i", "HR:0"), Tag("neg", "HR:1"), Tag("f", "HR:2:FLOAT"), Tag("bit", "HR:4.3"),
			Tag("coil", "CO:3"), Tag("scaled", "HR:0", new Scaling(0, 2000, 0, 100))
		});
		Assert.All(c, x => Assert.Null(x.Error));

		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 6 && d.Values.All(x => x.IsGood));
		Assert.Equal(1234L, v[1].Value);
		Assert.Equal(-1L, v[2].Value);
		Assert.Equal(12.5, v[3].Value);
		Assert.Equal(true, v[4].Value);
		Assert.Equal(true, v[5].Value);
		Assert.Equal(61.7, (double)v[6].Value!, 6);

		plc.SetFloat(2, -3.25f);
		v = await Until(s, d => Equals(d[3].Value, -3.25), v);

		// One point for HR:0 even though two tags read it, and one request for
		// the registers plus one for the coil.
		var worker = engine.Workers.Single();
		Assert.Equal(5, worker.PointCount);
		Assert.Equal(2, worker.RequestsPerCycle);
	}

	[Fact]
	public async Task WordSwappedFloats()
	{
		// 10.0f = 0x41200000; word-swapped on the wire as 0000 4120.
		plc.SetRegister(10, 0x0000);
		plc.SetRegister(11, 0x4120);

		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device("MLE") }, new[] { Tag("f", "HR:10:FLOAT") });
		Assert.Equal("HR:10:FLOAT:MLE", c[0].Normalized);
		s.Subscribe(new[] { 1 }, 50);

		var v = await Until(s, d => d.TryGetValue(1, out var x) && x.IsGood);
		Assert.Equal(10.0, v[1].Value);
	}

	[Fact]
	public async Task WritesReachTheDevice()
	{
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[]
		{
			Tag("reg", "HR:20"), Tag("f", "HR:22:FLOAT"), Tag("coil", "CO:9"), Tag("bit", "HR:30.4"),
			Tag("in", "IR:0"), Tag("sp", "HR:40", new Scaling(0, 1000, 0, 100))
		});
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);
		await Until(s, d => d.Count == 6);

		var r = await s.WriteAsync(new (int, object?)[]
		{
			(1, 321.0), (2, 1.5), (3, true), (4, true), (5, 1.0), (6, 42.5)
		}, TimeSpan.FromSeconds(5));

		Assert.True(r[1].Ok, r[1].Error);
		Assert.True(r[2].Ok, r[2].Error);
		Assert.True(r[3].Ok, r[3].Error);
		Assert.True(r[4].Ok, r[4].Error);
		Assert.Equal("Tag is read-only", r[5].Error);
		Assert.True(r[6].Ok, r[6].Error);

		Assert.Equal(321, plc.GetRegister(20));
		Assert.Equal(1.5f, BinaryPrimitives.ReadSingleBigEndian(plc.Registers.Slice(44)));
		Assert.True(plc.GetCoil(9));
		Assert.Equal(0x0010, plc.GetRegister(30));
		Assert.Equal(425, plc.GetRegister(40));

		// And the new values come back to the subscriber.
		await Until(s, d => Equals(d[1].Value, 321L) && Equals(d[3].Value, true));
	}

	[Fact]
	public async Task IllegalAddressMarksOnlyThatPoint()
	{
		plc.SetRegister(0, 5);
		plc.SetRegister(3, 6);
		plc.Server.RequestValidator = (unit, fc, address, quantity) =>
			address <= 2 && address + quantity > 2 ? ModbusExceptionCode.IllegalDataAddress :
				ModbusExceptionCode.OK;

		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[] { Tag("a", "HR:0"), Tag("hole", "HR:2"), Tag("b", "HR:3") });
		s.Subscribe(c.Select(x => x.Handle).ToList(), 50);

		var v = await Until(s, d => d.Count == 3 && d[1].IsGood && d[3].IsGood &&
			d[2].Status == Status.Device);

		Assert.Equal(5L, v[1].Value);
		Assert.Equal(6L, v[3].Value);
		Assert.Contains("exception 2", v[2].Error);
		Assert.Equal(DeviceState.Connected, engine.Workers.Single().State);
	}

	[Fact]
	public async Task ServerGoingAwayIsCommsBadAndComingBackRecovers()
	{
		plc.SetRegister(0, 77);
		var s = engine.CreateSession();
		var c = s.Configure(new[] { Device() }, new[] { Tag("a", "HR:0") });
		s.Subscribe(new[] { 1 }, 50);

		var v = await Until(s, d => d.TryGetValue(1, out var x) && x.IsGood);

		plc.Server.Stop();
		v = await Until(s, d => d[1].Status == Status.Comm, v);
		Assert.Equal(Quality.Bad, v[1].Quality);

		using var again = new TestModbusServer(plc.Port);
		BinaryPrimitives.WriteUInt16BigEndian(again.Registers, 88);

		v = await Until(s, d => d[1].IsGood && Equals(d[1].Value, 88L), v);
		Assert.True(engine.Workers.Single().Reconnects >= 1);
	}

	[Fact]
	public async Task NobodyListeningMeansComm()
	{
		var s = engine.CreateSession();
		var dead = Device() with { Port = TestModbusServer.FreePort() };
		s.Configure(new[] { dead }, new[] { Tag("a", "HR:0") });
		s.Subscribe(new[] { 1 }, 50);

		var v = await Until(s, d => d.TryGetValue(1, out var x) && x.Status == Status.Comm);
		Assert.NotNull(v[1].Error);
		Assert.Equal(DeviceState.Backoff, engine.Workers.Single().State);
	}
}
