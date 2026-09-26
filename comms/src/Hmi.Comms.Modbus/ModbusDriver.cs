using Hmi.Comms.Core;

namespace Hmi.Comms.Modbus;

/// <summary>
/// Modbus TCP.
///
/// Device options:
///   unitId           unit identifier, 0-255 (default 1)
///   byteOrder        default order for multi-register values: BE, LE, MBE, MLE (default BE)
///   maxGapRegisters  largest hole a register read may span (default 16)
///   maxGapCoils      largest hole a coil read may span (default 64)
///   maxRegsPerRead   registers per request, 1-125 (default 125)
///   maxCoilsPerRead  coils per request, 1-2000 (default 2000)
///
/// Devices on the same host and port -- units behind one gateway -- share one
/// socket; the unit id travels with each request.
/// </summary>
public sealed class ModbusDriver : IProtocolDriver
{
	public const int Port = 502;

	public string Protocol => "modbus";

	public int DefaultPort => Port;

	public string? ValidateDevice(DeviceConfig device)
	{
		if (string.IsNullOrWhiteSpace(device.Host))
		{
			return "Host is required";
		}

		if (device.Port is < 1 or > 65535)
		{
			return "Port must be 1 to 65535";
		}

		int unit = device.IntOption("unitId", 1);

		if (unit is < 0 or > 255)
		{
			return "unitId must be 0 to 255";
		}

		string? order = device.Option("byteOrder");

		if (order != null && !Enum.TryParse<ByteOrder>(order, true, out _))
		{
			return $"Unknown byteOrder '{order}' (BE, LE, MBE or MLE)";
		}

		if (device.IntOption("maxRegsPerRead", 125) is < 1 or > 125)
		{
			return "maxRegsPerRead must be 1 to 125";
		}

		if (device.IntOption("maxCoilsPerRead", 2000) is < 1 or > 2000)
		{
			return "maxCoilsPerRead must be 1 to 2000";
		}

		if (device.IntOption("maxGapRegisters", 16) < 0 || device.IntOption("maxGapCoils", 64) < 0)
		{
			return "Gap limits cannot be negative";
		}

		return null;
	}

	public string ConnectionKey(DeviceConfig device)
	{
		return device.Host.Trim().ToLowerInvariant() + ":" + (device.Port ?? Port);
	}

	public ParseResult Parse(DeviceConfig device, string address, string? dataTypeHint)
	{
		byte unit = (byte)Math.Clamp(device.IntOption("unitId", 1), 0, 255);
		var order = Enum.TryParse<ByteOrder>(device.Option("byteOrder") ?? "BE", true, out var o) ?
			o : ByteOrder.BE;

		return ModbusAddress.Parse(address, unit, order, dataTypeHint);
	}

	public static ModbusLimits Limits(DeviceConfig device) => new(
		MaxGapRegisters: Math.Max(0, device.IntOption("maxGapRegisters", 16)),
		MaxGapCoils: Math.Max(0, device.IntOption("maxGapCoils", 64)),
		MaxRegistersPerRead: Math.Clamp(device.IntOption("maxRegsPerRead", 125), 1, 125),
		MaxCoilsPerRead: Math.Clamp(device.IntOption("maxCoilsPerRead", 2000), 1, 2000));

	public IDeviceConnection Create(DeviceConfig device)
	{
		return new ModbusConnection(device, Limits(device));
	}
}
