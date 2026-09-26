using Hmi.Comms.Core;

namespace Hmi.Comms.Slc;

/// <summary>
/// SLC 5/05 and MicroLogix over EtherNet/IP, carrying PCCC.
///
/// Device options:
///   maxBytesPerRequest  data per read, up to 236 (default 236)
///   maxGapElements      largest hole a read may span (default 8)
///   swapStringBytes     true if ST and A characters arrive byte-swapped
/// </summary>
public sealed class SlcDriver : IProtocolDriver
{
	public const int Port = 44818;

	public string Protocol => "slc";

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

		if (device.IntOption("maxBytesPerRequest", 236) is < 2 or > 236)
		{
			return "maxBytesPerRequest must be 2 to 236";
		}

		if (device.IntOption("maxGapElements", 8) < 0)
		{
			return "maxGapElements cannot be negative";
		}

		return null;
	}

	public string ConnectionKey(DeviceConfig device)
	{
		return device.Host.Trim().ToLowerInvariant() + ":" + (device.Port ?? Port);
	}

	public ParseResult Parse(DeviceConfig device, string address, string? dataTypeHint)
	{
		return SlcAddress.Parse(address);
	}

	public static SlcLimits Limits(DeviceConfig device) => new(
		MaxBytesPerRequest: Math.Clamp(device.IntOption("maxBytesPerRequest", 236), 2, 236),
		MaxGapElements: Math.Max(0, device.IntOption("maxGapElements", 8)));

	public IDeviceConnection Create(DeviceConfig device)
	{
		return new SlcConnection(device, Limits(device));
	}
}
