using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FluentModbus;
using Hmi.Comms.Core;

namespace Hmi.Comms.Modbus;

/// <summary>The device answered with a Modbus exception rather than data.</summary>
public sealed class ModbusDeviceException : Exception
{
	public ModbusDeviceException(ModbusExceptionCode code, string message) : base(message)
	{
		Code = code;
	}

	public ModbusExceptionCode Code { get; }
}

/// <summary>
/// The seam between the planner and the wire, so tests can drive the read
/// logic without a socket. Device refusals surface as ModbusDeviceException;
/// anything else thrown means the connection is in trouble.
/// </summary>
public interface IModbusTransport : IDisposable
{
	void Connect(IPEndPoint endpoint, int timeoutMs);
	bool IsConnected { get; }
	byte[] ReadRegisters(byte unit, ModbusTable table, int start, int count);
	byte[] ReadBits(byte unit, ModbusTable table, int start, int count);
	void WriteCoil(byte unit, int address, bool value);
	void WriteRegisters(byte unit, int start, byte[] wire);
}

/// <summary>FluentModbus behind IModbusTransport.</summary>
public sealed class FluentTransport : IModbusTransport
{
	private readonly ModbusTcpClient client = new();

	public void Connect(IPEndPoint endpoint, int timeoutMs)
	{
		client.ConnectTimeout = timeoutMs;
		client.ReadTimeout = timeoutMs;
		client.WriteTimeout = timeoutMs;
		client.Connect(endpoint, ModbusEndianness.BigEndian);
	}

	public bool IsConnected => client.IsConnected;

	public byte[] ReadRegisters(byte unit, ModbusTable table, int start, int count)
	{
		return Call(() => table == ModbusTable.HR ?
			client.ReadHoldingRegisters(unit, (ushort)start, (ushort)count).ToArray() :
			client.ReadInputRegisters(unit, (ushort)start, (ushort)count).ToArray());
	}

	public byte[] ReadBits(byte unit, ModbusTable table, int start, int count)
	{
		return Call(() => table == ModbusTable.CO ?
			client.ReadCoils(unit, start, count).ToArray() :
			client.ReadDiscreteInputs(unit, start, count).ToArray());
	}

	public void WriteCoil(byte unit, int address, bool value)
	{
		Call(() =>
		{
			client.WriteSingleCoil(unit, address, value);

			return true;
		});
	}

	public void WriteRegisters(byte unit, int start, byte[] wire)
	{
		Call(() =>
		{
			if (wire.Length == 2)
			{
				client.WriteSingleRegister(unit, (ushort)start, wire);
			}
			else
			{
				client.WriteMultipleRegisters(unit, (ushort)start, wire);
			}

			return true;
		});
	}

	private static T Call<T>(Func<T> f)
	{
		try
		{
			return f();
		}
		catch (ModbusException e)
		{
			throw new ModbusDeviceException(e.ExceptionCode, e.Message);
		}
	}

	public void Dispose()
	{
		try
		{
			client.Disconnect();
		}
		catch (Exception)
		{
			// Closing a dead socket.
		}

		client.Dispose();
	}
}

public sealed class ModbusConnection : IDeviceConnection
{
	public const int RetryRefusedMs = 60_000;
	public const int RemergeMs = 10 * 60_000;

	private readonly DeviceConfig device;
	private readonly ModbusLimits limits;
	private readonly IModbusTransport transport;
	private readonly Func<long> ticks;

	public ModbusConnection(DeviceConfig device, ModbusLimits limits, IModbusTransport? transport = null,
		Func<long>? ticks = null)
	{
		this.device = device;
		this.limits = limits;
		this.transport = transport ?? new FluentTransport();
		this.ticks = ticks ?? (() => Environment.TickCount64);
	}

	public void Connect()
	{
		var endpoint = Resolve(device.Host, device.Port ?? ModbusDriver.Port);
		transport.Connect(endpoint, device.TimeoutMs);
	}

	internal static IPEndPoint Resolve(string host, int port)
	{
		if (IPAddress.TryParse(host, out var ip))
		{
			return new IPEndPoint(ip, port);
		}

		var addresses = Dns.GetHostAddresses(host);
		var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

		return new IPEndPoint(v4 ?? addresses.First(), port);
	}

	public IReadPlan Plan(IReadOnlyList<Point> points)
	{
		return new ModbusPlan { Points = points, Blocks = ModbusBlockBuilder.Build(points, limits) };
	}

	public void Read(IReadPlan p, IReadSink sink)
	{
		var plan = (ModbusPlan)p;
		long now = ticks();

		if (!transport.IsConnected)
		{
			throw new CommsLostException("Not connected");
		}

		if (plan.SplitAt != 0 && now - plan.SplitAt > RemergeMs)
		{
			plan.Blocks = ModbusBlockBuilder.Build(plan.Points, limits);
			plan.SplitAt = 0;
		}

		var result = new List<ModbusBlock>(plan.Blocks.Count);

		foreach (var block in plan.Blocks)
		{
			ReadBlock(plan, block, sink, result, now);
		}

		plan.Blocks = result;
	}

	/// <summary>
	/// Reads one block. When the device refuses the range -- usually a hole
	/// in its register map inside the block -- the block is split in two and
	/// each half tried, down to single points; only a point the device refuses
	/// on its own is marked bad, and it is left alone for a minute.
	/// </summary>
	private void ReadBlock(ModbusPlan plan, ModbusBlock block, IReadSink sink, List<ModbusBlock> result,
		long now)
	{
		if (block.RetryAfter > now)
		{
			result.Add(block);

			return;
		}

		try
		{
			bool bits = block.Table is ModbusTable.CO or ModbusTable.DI;
			byte[] data = bits ?
				transport.ReadBits(block.Unit, block.Table, block.Start, block.Count) :
				transport.ReadRegisters(block.Unit, block.Table, block.Start, block.Count);

			foreach (var point in block.Points)
			{
				var a = (ModbusAddress)point.Address;
				int offset = a.Offset - block.Start;

				if (bits)
				{
					sink.Set(point, ModbusCodec.CoilBit(data, offset));
				}
				else
				{
					sink.Set(point, ModbusCodec.Decode(a, data.AsSpan(offset * 2, a.Count * 2)));
				}
			}

			block.RetryAfter = 0;
			result.Add(block);
		}
		catch (ModbusDeviceException e) when (IsAddressProblem(e.Code))
		{
			if (block.Points.Count > 1)
			{
				plan.SplitAt = now;
				int half = block.Points.Count / 2;

				foreach (var part in new[] { block.Points.Take(half), block.Points.Skip(half) })
				{
					foreach (var sub in ModbusBlockBuilder.Build(part, limits))
					{
						ReadBlock(plan, sub, sink, result, now);
					}
				}

				return;
			}

			string message = Describe(e.Code);
			sink.Fail(block.Points[0], Status.Device, message);
			block.RetryAfter = now + RetryRefusedMs;
			result.Add(block);
		}
		catch (ModbusDeviceException e)
		{
			// The device (or the gateway in front of it) answered, but not with
			// data: busy, failed, target unreachable. Only these points suffer;
			// the connection itself is fine.
			string message = Describe(e.Code);

			foreach (var point in block.Points)
			{
				sink.Fail(point, Status.Device, message);
			}

			result.Add(block);
		}
	}

	private static bool IsAddressProblem(ModbusExceptionCode code) =>
		code is ModbusExceptionCode.IllegalDataAddress or ModbusExceptionCode.IllegalDataValue or
			ModbusExceptionCode.IllegalFunction;

	private static string Describe(ModbusExceptionCode code) => code switch
	{
		ModbusExceptionCode.IllegalFunction => "Illegal function (exception 1)",
		ModbusExceptionCode.IllegalDataAddress => "Illegal data address (exception 2)",
		ModbusExceptionCode.IllegalDataValue => "Illegal data value (exception 3)",
		ModbusExceptionCode.ServerDeviceFailure => "Device failure (exception 4)",
		ModbusExceptionCode.Acknowledge => "Acknowledge, busy (exception 5)",
		ModbusExceptionCode.ServerDeviceBusy => "Device busy (exception 6)",
		ModbusExceptionCode.GatewayPathUnavailable => "Gateway path unavailable (exception 10)",
		ModbusExceptionCode.GatewayTargetDeviceFailedToRespond => "Gateway target did not respond (exception 11)",
		_ => $"Modbus exception {(int)code}"
	};

	public WriteOutcome[] Write(IReadOnlyList<WriteItem> items)
	{
		var results = new WriteOutcome[items.Count];

		for (int i = 0; i < items.Count; i++)
		{
			results[i] = WriteOne(items[i]);
		}

		return results;
	}

	private WriteOutcome WriteOne(WriteItem item)
	{
		var a = (ModbusAddress)item.Point.Address;

		try
		{
			switch (a.Table)
			{
				case ModbusTable.CO:
					transport.WriteCoil(a.Unit, a.Offset, (bool)item.Raw!);
					break;

				case ModbusTable.HR when a.Bit >= 0:
					// Read-modify-write: Modbus has no single-bit register write
					// in common use (FC22 is optional), so a change the device
					// makes to the other bits in between would be overwritten.
					byte[] current = transport.ReadRegisters(a.Unit, ModbusTable.HR, a.Offset, 1);
					Span<byte> be = stackalloc byte[2];
					ModbusCodec.Reorder(current, be, a.Order);
					ushort reg = BinaryPrimitives.ReadUInt16BigEndian(be);
					reg = (bool)item.Raw! ? (ushort)(reg | (1 << a.Bit)) : (ushort)(reg & ~(1 << a.Bit));
					BinaryPrimitives.WriteUInt16BigEndian(be, reg);
					var wire = new byte[2];
					ModbusCodec.Reorder(be, wire, a.Order);
					transport.WriteRegisters(a.Unit, a.Offset, wire);
					break;

				case ModbusTable.HR:
					transport.WriteRegisters(a.Unit, a.Offset, ModbusCodec.Encode(a, item.Raw));
					break;

				default:
					return WriteOutcome.Fail($"{a.Table} is read-only");
			}

			return WriteOutcome.Success;
		}
		catch (ModbusDeviceException e)
		{
			return WriteOutcome.Fail(Describe(e.Code));
		}
		catch (OverflowException)
		{
			return WriteOutcome.Fail($"Out of range for {a.MType}");
		}
	}

	public void Dispose()
	{
		transport.Dispose();
	}
}
