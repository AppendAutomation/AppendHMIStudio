using System.Net;
using FluentModbus;
using Hmi.Comms.Core;
using Hmi.Comms.Modbus;

namespace Hmi.Comms.Modbus.Tests;

/// <summary>A register map with holes, behind the transport seam.</summary>
internal sealed class FakeTransport : IModbusTransport
{
	public readonly HashSet<int> Missing = new();
	public readonly List<string> Requests = new();
	public readonly byte[] Registers = new byte[2 * 65536];
	public bool Connected = true;
	public ModbusExceptionCode? Refuse;

	public void Connect(IPEndPoint endpoint, int timeoutMs) => Connected = true;

	public bool IsConnected => Connected;

	public byte[] ReadRegisters(byte unit, ModbusTable table, int start, int count)
	{
		Requests.Add($"{start}+{count}");

		if (Refuse is ModbusExceptionCode code)
		{
			throw new ModbusDeviceException(code, "refused");
		}

		for (int i = start; i < start + count; i++)
		{
			if (Missing.Contains(i))
			{
				throw new ModbusDeviceException(ModbusExceptionCode.IllegalDataAddress, "hole");
			}
		}

		return Registers.AsSpan(start * 2, count * 2).ToArray();
	}

	public byte[] ReadBits(byte unit, ModbusTable table, int start, int count) => new byte[(count + 7) / 8];

	public void WriteCoil(byte unit, int address, bool value)
	{
	}

	public void WriteRegisters(byte unit, int start, byte[] wire)
	{
		wire.CopyTo(Registers, start * 2);
	}

	public void Dispose()
	{
	}
}

internal sealed class Collect : IReadSink
{
	public readonly Dictionary<string, TagValue> Values = new();

	public void Set(Point point, object? raw) => Values[point.Address.Normalized] = TagValue.Good(raw, 0);

	public void Fail(Point point, string status, string error) =>
		Values[point.Address.Normalized] = TagValue.Bad(status, error, 0);
}

public sealed class SplitTests
{
	private readonly DeviceConfig device = new() { Name = "t", Protocol = "modbus", Host = "x" };
	private long now = 1_000_000;

	private (ModbusConnection, FakeTransport, List<Point>) Rig(params string[] addrs)
	{
		var transport = new FakeTransport();
		var conn = new ModbusConnection(device, new ModbusLimits(), transport, () => now);
		var worker = new DeviceWorker("t", new ModbusDriver(), device);
		var points = addrs.Select(a => worker.Acquire(ModbusAddress.Parse(a, 1, ByteOrder.BE, null).Address!))
			.ToList();

		return (conn, transport, points);
	}

	[Fact]
	public void AHoleSplitsTheBlockAndOnlyTheMissingPointIsBad()
	{
		var (conn, t, points) = Rig("HR:0", "HR:1", "HR:5", "HR:6");
		t.Registers[1] = 7;
		t.Registers[13] = 9;
		t.Missing.Add(5);

		var plan = (ModbusPlan)conn.Plan(points);
		Assert.Single(plan.Blocks);

		var sink = new Collect();
		conn.Read(plan, sink);

		Assert.Equal(7L, sink.Values["HR:0:INT16:BE"].Value);
		Assert.Equal(9L, sink.Values["HR:6:INT16:BE"].Value);
		Assert.Equal(Status.Device, sink.Values["HR:5:INT16:BE"].Status);
		Assert.Contains("exception 2", sink.Values["HR:5:INT16:BE"].Error);

		// The plan remembers the split: the next pass goes straight to the parts
		// that work and skips the refused point for a while.
		t.Requests.Clear();
		conn.Read(plan, new Collect());
		Assert.Equal(new[] { "0+2", "6+1" }, t.Requests);

		now += ModbusConnection.RetryRefusedMs + 1;
		t.Requests.Clear();
		conn.Read(plan, new Collect());
		Assert.Equal(new[] { "0+2", "5+1", "6+1" }, t.Requests);
	}

	[Fact]
	public void TheWholePlanIsRebuiltLaterInCaseTheMapChanged()
	{
		var (conn, t, points) = Rig("HR:0", "HR:1", "HR:2", "HR:3");
		t.Missing.Add(2);
		var plan = (ModbusPlan)conn.Plan(points);
		conn.Read(plan, new Collect());
		Assert.True(plan.Blocks.Count > 1);

		t.Missing.Clear();
		now += ModbusConnection.RemergeMs + 1;
		t.Requests.Clear();
		conn.Read(plan, new Collect());

		Assert.Equal(new[] { "0+4" }, t.Requests);
		Assert.Single(plan.Blocks);
	}

	[Fact]
	public void BusyDeviceFailsTheBlockWithoutSplitting()
	{
		var (conn, t, points) = Rig("HR:0", "HR:1");
		t.Refuse = ModbusExceptionCode.ServerDeviceBusy;
		var plan = (ModbusPlan)conn.Plan(points);
		var sink = new Collect();

		conn.Read(plan, sink);

		Assert.Single(t.Requests);
		Assert.All(sink.Values.Values, v => Assert.Equal(Status.Device, v.Status));
		Assert.Contains("busy", sink.Values["HR:0:INT16:BE"].Error);
	}

	[Fact]
	public void LostSocketIsAConnectionFailure()
	{
		var (conn, t, points) = Rig("HR:0");
		t.Connected = false;

		Assert.Throws<CommsLostException>(() => conn.Read(conn.Plan(points), new Collect()));
	}

	[Fact]
	public void BitWriteIsReadModifyWrite()
	{
		var (conn, t, points) = Rig("HR:3.2", "HR:3.0");
		t.Registers[6] = 0x80;
		t.Registers[7] = 0x01;

		var r = conn.Write(new[] { new WriteItem(points[0], true) });
		Assert.True(r[0].Ok);
		Assert.Equal(0x80, t.Registers[6]);
		Assert.Equal(0x05, t.Registers[7]);

		conn.Write(new[] { new WriteItem(points[1], false) });
		Assert.Equal(0x04, t.Registers[7]);
	}
}
