using Hmi.Comms.Core;
using Hmi.Comms.Modbus;

namespace Hmi.Comms.Modbus.Tests;

public sealed class AddressTests
{
	private static ParseResult P(string s, string? hint = null, ByteOrder order = ByteOrder.BE) =>
		ModbusAddress.Parse(s, 1, order, hint);

	[Theory]
	[InlineData("HR:100", "HR:100:INT16:BE", DataType.Int16)]
	[InlineData("hr:100:float", "HR:100:FLOAT:BE", DataType.Float32)]
	[InlineData("IR:5:UINT32:MLE", "IR:5:UINT32:MLE", DataType.UInt32)]
	[InlineData("HR:7:MBE", "HR:7:INT16:MBE", DataType.Int16)]
	[InlineData("HR:10.3", "HR:10.3", DataType.Bool)]
	[InlineData("HR:10.15:BOOL", "HR:10.15", DataType.Bool)]
	[InlineData("CO:0", "CO:0", DataType.Bool)]
	[InlineData("DI:17:BOOL", "DI:17", DataType.Bool)]
	[InlineData("HR:200:STRING20", "HR:200:STRING20:BE", DataType.String)]
	[InlineData("HR:65532:DOUBLE", "HR:65532:DOUBLE:BE", DataType.Float64)]
	[InlineData(" HR:1:int64:le ", "HR:1:INT64:LE", DataType.Int64)]
	public void Valid(string text, string normalized, DataType type)
	{
		var r = P(text);

		Assert.Null(r.Error);
		Assert.Equal(normalized, r.Address!.Normalized);
		Assert.Equal(type, r.Address.Type);
	}

	[Theory]
	[InlineData("40001", "Use HR:0 for 40001")]
	[InlineData("400101", "Use HR:100 for 400101")]
	[InlineData("30010", "Use IR:9 for 30010")]
	[InlineData("10001", "Use DI:0 for 10001")]
	[InlineData("00005", "Use CO:4 for 00005")]
	[InlineData("XR:1", "Expected HR|IR|CO|DI")]
	[InlineData("HR:70000", "Offset must be 0 to 65535")]
	[InlineData("HR:1.16", "Bit must be 0 to 15")]
	[InlineData("HR:1.3:INT16", "A bit of a register is BOOL")]
	[InlineData("CO:1.2", "no .bit suffix")]
	[InlineData("CO:1:INT16", "only BOOL applies")]
	[InlineData("HR:1:BOOL", "use HR:1.0 for bit 0")]
	[InlineData("HR:1:WIDGET", "Unknown type 'WIDGET'")]
	[InlineData("HR:1:FLOAT:XY", "Unknown byte order 'XY'")]
	[InlineData("HR:1:STRING", "STRING needs a length")]
	[InlineData("HR:1:STRING999", "STRING needs a length")]
	[InlineData("HR:65535:FLOAT", "runs past register 65535")]
	public void Invalid(string text, string message)
	{
		var r = P(text);

		Assert.Null(r.Address);
		Assert.Contains(message, r.Error);
	}

	[Fact]
	public void HintSuppliesTheTypeWhenTheAddressDoesNot()
	{
		Assert.Equal(DataType.Float32, P("HR:1", "REAL").Address!.Type);
		Assert.Equal(DataType.Int32, P("HR:1", "DINT").Address!.Type);
		Assert.Equal(DataType.Int16, P("HR:1", "BOOL").Address!.Type);
		Assert.Equal(DataType.Int16, P("HR:1:INT16", "REAL").Address!.Type);
		Assert.Contains("needs a length", P("HR:1", "STRING").Error);
	}

	[Fact]
	public void DeviceDefaultOrderAppliesAndCanBeOverridden()
	{
		Assert.Equal("HR:1:FLOAT:MLE", P("HR:1:FLOAT", order: ByteOrder.MLE).Address!.Normalized);
		Assert.Equal("HR:1:FLOAT:BE", P("HR:1:FLOAT:BE", order: ByteOrder.MLE).Address!.Normalized);
	}

	[Fact]
	public void UnitIsPartOfTheKeyNotTheName()
	{
		var a = ModbusAddress.Parse("HR:1", 1, ByteOrder.BE, null).Address!;
		var b = ModbusAddress.Parse("HR:1", 2, ByteOrder.BE, null).Address!;

		Assert.Equal(a.Normalized, b.Normalized);
		Assert.NotEqual(a.Key, b.Key);
	}

	[Fact]
	public void WritabilityFollowsTheTable()
	{
		Assert.True(P("HR:1").Address!.Writable);
		Assert.True(P("CO:1").Address!.Writable);
		Assert.False(P("IR:1").Address!.Writable);
		Assert.False(P("DI:1").Address!.Writable);
	}
}

public sealed class CodecTests
{
	private static ModbusAddress A(string s) => (ModbusAddress)ModbusAddress.Parse(s, 1, ByteOrder.BE, null).Address!;

	private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

	// 10.0f is 0x41200000.
	[Theory]
	[InlineData("BE", "41 20 00 00")]
	[InlineData("LE", "00 00 20 41")]
	[InlineData("MBE", "20 41 00 00")]
	[InlineData("MLE", "00 00 41 20")]
	public void Float(string order, string wire)
	{
		var a = A($"HR:0:FLOAT:{order}");

		Assert.Equal(10.0, ModbusCodec.Decode(a, H(wire)));
		Assert.Equal(H(wire), ModbusCodec.Encode(a, 10.0));
	}

	[Theory]
	[InlineData("BE", "12 34")]
	[InlineData("LE", "34 12")]
	[InlineData("MBE", "34 12")]
	[InlineData("MLE", "12 34")]
	public void Int16(string order, string wire)
	{
		var a = A($"HR:0:INT16:{order}");

		Assert.Equal(0x1234L, ModbusCodec.Decode(a, H(wire)));
		Assert.Equal(H(wire), ModbusCodec.Encode(a, 0x1234L));
	}

	[Theory]
	[InlineData("BE", "01 02 03 04 05 06 07 08")]
	[InlineData("LE", "08 07 06 05 04 03 02 01")]
	[InlineData("MBE", "02 01 04 03 06 05 08 07")]
	[InlineData("MLE", "07 08 05 06 03 04 01 02")]
	public void Int64(string order, string wire)
	{
		var a = A($"HR:0:INT64:{order}");

		Assert.Equal(0x0102030405060708L, ModbusCodec.Decode(a, H(wire)));
		Assert.Equal(H(wire), ModbusCodec.Encode(a, 0x0102030405060708L));
	}

	[Fact]
	public void SignedAndUnsigned()
	{
		Assert.Equal(-1L, ModbusCodec.Decode(A("HR:0:INT16"), H("FF FF")));
		Assert.Equal(65535L, ModbusCodec.Decode(A("HR:0:UINT16"), H("FF FF")));
		Assert.Equal(-2L, ModbusCodec.Decode(A("HR:0:INT32"), H("FF FF FF FE")));
		Assert.Equal(4294967294L, ModbusCodec.Decode(A("HR:0:UINT32"), H("FF FF FF FE")));
		Assert.Equal(ulong.MaxValue, ModbusCodec.Decode(A("HR:0:UINT64"), H("FF FF FF FF FF FF FF FF")));
		Assert.Equal(1.5, ModbusCodec.Decode(A("HR:0:DOUBLE"), H("3F F8 00 00 00 00 00 00")));
	}

	[Fact]
	public void BitOfRegisterFollowsTheLogicalValue()
	{
		// Register value 0x0008: bit 3 set.
		Assert.Equal(true, ModbusCodec.Decode(A("HR:0.3"), H("00 08")));
		Assert.Equal(false, ModbusCodec.Decode(A("HR:0.2"), H("00 08")));

		// Byte-swapped device: same logical value on the wire as 08 00.
		var swapped = (ModbusAddress)ModbusAddress.Parse("HR:0.3", 1, ByteOrder.MBE, null).Address!;
		Assert.Equal(true, ModbusCodec.Decode(swapped, H("08 00")));
	}

	[Fact]
	public void Strings()
	{
		var be = A("HR:0:STRING5");
		Assert.Equal(3, be.Count);
		Assert.Equal("HELLO", ModbusCodec.Decode(be, H("48 45 4C 4C 4F 00")));
		Assert.Equal(H("48 45 4C 4C 4F 00"), ModbusCodec.Encode(be, "HELLO"));
		Assert.Equal("HI", ModbusCodec.Decode(be, H("48 49 00 00 00 00")));

		var swapped = A("HR:0:STRING4:MBE");
		Assert.Equal("ABCD", ModbusCodec.Decode(swapped, H("42 41 44 43")));
		Assert.Equal(H("42 41 44 43"), ModbusCodec.Encode(swapped, "ABCD"));
	}

	[Fact]
	public void EncodeRefusesOverflow()
	{
		Assert.Throws<OverflowException>(() => ModbusCodec.Encode(A("HR:0:INT16"), 40000L));
	}

	[Fact]
	public void Coils()
	{
		var packed = new byte[] { 0b0000_0101, 0b1000_0000 };

		Assert.True(ModbusCodec.CoilBit(packed, 0));
		Assert.False(ModbusCodec.CoilBit(packed, 1));
		Assert.True(ModbusCodec.CoilBit(packed, 2));
		Assert.True(ModbusCodec.CoilBit(packed, 15));
	}
}

public sealed class BlockBuilderTests
{
	/// <summary>Points need a worker to exist; a throwaway one hands them out.</summary>
	private static List<Point> Points(params string[] addrs) => PointsOn(1, addrs);

	private static List<Point> PointsOn(byte unit, params string[] addrs)
	{
		var device = new DeviceConfig { Name = "t", Protocol = "modbus", Host = "x" };
		var worker = new DeviceWorker("t", new ModbusDriver(), device);

		return addrs.Select(a => worker.Acquire(ModbusAddress.Parse(a, unit, ByteOrder.BE, null).Address!))
			.ToList();
	}

	private static string Describe(List<ModbusBlock> blocks) =>
		string.Join(" | ", blocks.Select(b => $"{b.Unit}/{b.Table}:{b.Start}+{b.Count}"));

	[Fact]
	public void AdjacentAndNearPointsMerge()
	{
		var b = ModbusBlockBuilder.Build(Points("HR:0", "HR:1", "HR:10", "HR:40"), new ModbusLimits());

		Assert.Equal("1/HR:0+11 | 1/HR:40+1", Describe(b));
	}

	[Fact]
	public void GapLimitIsRespected()
	{
		var limits = new ModbusLimits(MaxGapRegisters: 3);

		Assert.Equal("1/HR:0+5", Describe(ModbusBlockBuilder.Build(Points("HR:0", "HR:4"), limits)));
		Assert.Equal("1/HR:0+1 | 1/HR:5+1", Describe(ModbusBlockBuilder.Build(Points("HR:0", "HR:5"), limits)));

		var none = new ModbusLimits(MaxGapRegisters: 0);
		Assert.Equal("1/HR:0+2 | 1/HR:3+1", Describe(ModbusBlockBuilder.Build(Points("HR:0", "HR:1", "HR:3"), none)));
	}

	[Fact]
	public void MultiRegisterValuesAreCoveredWhole()
	{
		var b = ModbusBlockBuilder.Build(Points("HR:0:DOUBLE", "HR:2", "HR:4:FLOAT"), new ModbusLimits());

		Assert.Equal("1/HR:0+6", Describe(b));
	}

	[Fact]
	public void RequestSizeIsCapped()
	{
		var b = ModbusBlockBuilder.Build(Points("HR:0", "HR:100", "HR:124", "HR:125"),
			new ModbusLimits(MaxGapRegisters: 200));

		Assert.Equal("1/HR:0+125 | 1/HR:125+1", Describe(b));

		var coils = ModbusBlockBuilder.Build(Points("CO:0", "CO:1999", "CO:2000"),
			new ModbusLimits(MaxGapCoils: 5000));
		Assert.Equal("1/CO:0+2000 | 1/CO:2000+1", Describe(coils));

		var small = ModbusBlockBuilder.Build(Points("HR:0:FLOAT", "HR:2:FLOAT"),
			new ModbusLimits(MaxRegistersPerRead: 3));
		Assert.Equal("1/HR:0+2 | 1/HR:2+2", Describe(small));
	}

	[Fact]
	public void TablesAndUnitsNeverMerge()
	{
		var mixed = Points("HR:0", "IR:1", "CO:2", "DI:3");
		mixed.AddRange(PointsOn(2, "HR:1"));

		Assert.Equal("1/HR:0+1 | 1/IR:1+1 | 1/CO:2+1 | 1/DI:3+1 | 2/HR:1+1",
			Describe(ModbusBlockBuilder.Build(mixed, new ModbusLimits())));
	}

	[Fact]
	public void BitsOfOneRegisterShareIt()
	{
		var b = ModbusBlockBuilder.Build(Points("HR:5.0", "HR:5.7", "HR:5"), new ModbusLimits());

		Assert.Equal("1/HR:5+1", Describe(b));
		Assert.Equal(3, b[0].Points.Count);
	}
}
