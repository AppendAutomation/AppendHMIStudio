using Hmi.Comms.Core;

namespace Hmi.Comms.Modbus;

public sealed record ModbusLimits(int MaxGapRegisters = 16, int MaxGapCoils = 64,
	int MaxRegistersPerRead = 125, int MaxCoilsPerRead = 2000);

/// <summary>One read request: a contiguous run of registers or coils on one unit.</summary>
public sealed class ModbusBlock
{
	public required byte Unit;
	public required ModbusTable Table;
	public required int Start;
	public int Count;
	public readonly List<Point> Points = new();

	/// <summary>A single point the device refused is left alone until this tick.</summary>
	public long RetryAfter;

	public int End => Start + Count - 1;

	public string Describe()
	{
		return $"unit {Unit} {Table} {Start}..{End} ({Count}, {Points.Count} points)";
	}
}

public sealed class ModbusPlan : IReadPlan
{
	public required IReadOnlyList<Point> Points;
	public required List<ModbusBlock> Blocks;

	/// <summary>When a block was last split; the plan is rebuilt whole after a while in
	/// case the device's map has changed.</summary>
	public long SplitAt;

	public int RequestCount => Blocks.Count;

	public IReadOnlyList<string> Describe() => Blocks.Select(b => b.Describe()).ToList();
}

/// <summary>
/// Coalesces points into as few requests as the limits allow. Points on the
/// same unit and table merge while the hole between them is no bigger than
/// the gap limit and the whole run fits in one request. The gap limit is what
/// keeps a merge from straying into registers the device does not have.
/// </summary>
public static class ModbusBlockBuilder
{
	/// <param name="barriers">Addresses the device refused, as (unit, table, offset): a block
	/// may not reach across one.</param>
	public static List<ModbusBlock> Build(IEnumerable<Point> points, ModbusLimits limits,
		ISet<(byte, ModbusTable, int)>? barriers = null)
	{
		var blocks = new List<ModbusBlock>();

		var groups = points
			.GroupBy(p => (((ModbusAddress)p.Address).Unit, ((ModbusAddress)p.Address).Table))
			.OrderBy(g => g.Key.Unit).ThenBy(g => g.Key.Table);

		foreach (var g in groups)
		{
			bool bits = g.Key.Table is ModbusTable.CO or ModbusTable.DI;
			int gap = bits ? limits.MaxGapCoils : limits.MaxGapRegisters;
			int max = bits ? limits.MaxCoilsPerRead : limits.MaxRegistersPerRead;
			ModbusBlock? block = null;

			foreach (var p in g.OrderBy(p => ((ModbusAddress)p.Address).Offset)
				.ThenBy(p => ((ModbusAddress)p.Address).End))
			{
				var a = (ModbusAddress)p.Address;

				if (block != null && a.Offset <= block.End + 1 + gap &&
					Math.Max(block.End, a.End) - block.Start + 1 <= max &&
					!Crosses(barriers, g.Key.Unit, g.Key.Table, block.End, a.Offset))
				{
					block.Count = Math.Max(block.End, a.End) - block.Start + 1;
					block.Points.Add(p);

					continue;
				}

				block = new ModbusBlock
				{
					Unit = g.Key.Unit,
					Table = g.Key.Table,
					Start = a.Offset,
					Count = a.Count
				};

				block.Points.Add(p);
				blocks.Add(block);
			}
		}

		return blocks;
	}

	private static bool Crosses(ISet<(byte, ModbusTable, int)>? barriers, byte unit, ModbusTable table,
		int from, int to)
	{
		if (barriers == null || barriers.Count == 0)
		{
			return false;
		}

		for (int a = from + 1; a < to; a++)
		{
			if (barriers.Contains((unit, table, a)))
			{
				return true;
			}
		}

		return false;
	}
}
