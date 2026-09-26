using Hmi.Comms.Core;

namespace Hmi.Comms.Logix;

/// <summary>
/// EtherNet/IP to ControlLogix, CompactLogix and Micro800 controllers.
///
/// Device options:
///   slot      processor slot in the chassis (default 0)
///   micro800  true for Micro800, which has no backplane routing
///
/// Tags are read in Multiple Service Packets sized to the connection the
/// controller grants (4002 bytes on firmware that allows a Large Forward
/// Open, 504 otherwise).
/// </summary>
public sealed class LogixDriver : IProtocolDriver
{
	public const int Port = 44818;

	public string Protocol => "logix";

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

		if (device.IntOption("slot", 0) is < 0 or > 255)
		{
			return "Slot must be 0 to 255";
		}

		return null;
	}

	public string ConnectionKey(DeviceConfig device)
	{
		string slot = device.BoolOption("micro800", false) ? "m800" : device.IntOption("slot", 0).ToString();

		return device.Host.Trim().ToLowerInvariant() + ":" + (device.Port ?? Port) + "/" + slot;
	}

	public ParseResult Parse(DeviceConfig device, string address, string? dataTypeHint)
	{
		return LogixAddress.Parse(address);
	}

	public IDeviceConnection Create(DeviceConfig device)
	{
		return new LogixConnection(device);
	}
}
