using Hmi.Comms.Core;

namespace Hmi.Comms.Core.Tests;

public sealed class SchedulerTests
{
	[Fact]
	public void UnsubscribedPointsAreNeverRead()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A"), Rig.Tag("b", "B") });

		rig.Step();
		rig.Step(1000);

		Assert.Empty(rig.Driver.Connections);
		Assert.Equal(0, rig.Driver.Connects);
	}

	[Fact]
	public void SubscribedPointsAreReadAtTheirRate()
	{
		using var rig = new Rig();
		rig.Driver.Values["A"] = 1.5;
		var s = rig.Engine.CreateSession();
		var cfg = s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A"), Rig.Tag("b", "B") });
		s.Subscribe(new[] { cfg[0].Handle }, 100);

		rig.Step();
		var conn = rig.Driver.Connections.Single();
		Assert.Single(conn.Reads);
		Assert.Equal(new[] { "A" }, conn.Reads[0]);

		rig.Step(50);
		Assert.Single(conn.Reads);

		rig.Step(50);
		Assert.Equal(2, conn.Reads.Count);
	}

	[Fact]
	public void PointsDueTogetherShareOnePlan()
	{
		using var rig = new Rig();
		var s1 = rig.Engine.CreateSession();
		var s2 = rig.Engine.CreateSession();
		var c1 = s1.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		var c2 = s2.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("b", "B") });
		s1.Subscribe(new[] { c1[0].Handle }, 250);
		s2.Subscribe(new[] { c2[0].Handle }, 1000);

		rig.Step();
		var conn = rig.Driver.Connections.Single();
		Assert.Equal(new[] { "A", "B" }, conn.Reads[0]);

		for (int i = 0; i < 3; i++)
		{
			rig.Step(250);
			Assert.Equal(new[] { "A" }, conn.Reads[^1]);
		}

		rig.Step(250);
		Assert.Equal(new[] { "A", "B" }, conn.Reads[^1]);

		// Two distinct due-sets, two plans, both reused.
		Assert.Equal(2, conn.PlansBuilt);
	}

	[Fact]
	public void SameAddressInTwoSessionsIsOnePointAndOneConnection()
	{
		using var rig = new Rig();
		var s1 = rig.Engine.CreateSession();
		var s2 = rig.Engine.CreateSession();
		var c1 = s1.Configure(new[] { Rig.Device("PLC") }, new[] { Rig.Tag("x", "a", "PLC") });
		var c2 = s2.Configure(new[] { Rig.Device("Other") }, new[] { Rig.Tag("y", "A", "Other") });
		s1.Subscribe(new[] { c1[0].Handle }, 100);
		s2.Subscribe(new[] { c2[0].Handle }, 500);

		rig.Step();

		Assert.Single(rig.Engine.Workers);
		Assert.Equal(1, rig.Worker.PointCount);
		Assert.Equal(new[] { "A" }, rig.Driver.Connections.Single().Reads[0]);
	}

	[Fact]
	public void FasterSubscriberWinsAndSlowerRemainsAfterItLeaves()
	{
		using var rig = new Rig();
		var s1 = rig.Engine.CreateSession();
		var s2 = rig.Engine.CreateSession();
		var c1 = s1.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		var c2 = s2.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		s1.Subscribe(new[] { c1[0].Handle }, 1000);
		s2.Subscribe(new[] { c2[0].Handle }, 100);

		rig.Step();
		rig.Step(100);
		Assert.Equal(2, rig.Driver.Connections.Single().Reads.Count);

		// The read already scheduled at the fast rate still happens; after that
		// the point follows the slower subscriber.
		s2.Unsubscribe();
		rig.Step(100);
		Assert.Equal(3, rig.Driver.Connections.Single().Reads.Count);
		rig.Step(500);
		Assert.Equal(3, rig.Driver.Connections.Single().Reads.Count);
		rig.Step(500);
		Assert.Equal(4, rig.Driver.Connections.Single().Reads.Count);
	}

	[Fact]
	public void MinimumScanRateIsEnforced()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[] { Rig.Device(minScan: 200) }, new[] { Rig.Tag("a", "A") });
		s.Subscribe(new[] { c[0].Handle }, 10);

		rig.Step();
		rig.Step(100);
		Assert.Single(rig.Driver.Connections.Single().Reads);
		rig.Step(100);
		Assert.Equal(2, rig.Driver.Connections.Single().Reads.Count);
	}

	[Fact]
	public async Task WritesGoFirstAndTheValueIsReadBack()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		s.Subscribe(new[] { c[0].Handle }, 1000);
		rig.Step();

		var write = s.WriteAsync(new[] { (c[0].Handle, (object?)42.0) }, TimeSpan.FromSeconds(5));
		rig.Step(10);

		var res = await write;
		Assert.True(res[c[0].Handle].Ok);
		Assert.Equal(42.0, rig.Driver.Values["A"]);

		// Read straight back rather than waiting out the 1 s schedule.
		var conn = rig.Driver.Connections.Single();
		Assert.Equal(2, conn.Reads.Count);
		Assert.Equal(42.0, s.Snapshot(new[] { c[0].Handle })[0].Value.Value);
	}

	[Fact]
	public async Task WritesAreCoercedAndChecked()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[] { Rig.Device() }, new[]
		{
			Rig.Tag("i", "INT16:I"),
			Rig.Tag("b", "BOOL:B"),
			Rig.Tag("ro", "RO:R"),
			Rig.Tag("cfgro", "C", readOnly: true),
			Rig.Tag("s", "STRING:S"),
			Rig.Tag("bad", "BAD:X")
		});
		s.Subscribe(c.Select(x => x.Handle).ToList(), 1000);
		rig.Step();

		var write = s.WriteAsync(new (int, object?)[]
		{
			(1, 12.6), (2, "on"), (3, 1.0), (4, 1.0), (5, "this is far too long"), (6, 1.0), (99, 1.0)
		}, TimeSpan.FromSeconds(5));
		rig.Step();
		var r = await write;

		Assert.True(r[1].Ok);
		Assert.Equal(13L, rig.Driver.Values["I"]);
		Assert.True(r[2].Ok);
		Assert.Equal(true, rig.Driver.Values["B"]);
		Assert.Equal("Tag is read-only", r[3].Error);
		Assert.Equal("Tag is read-only", r[4].Error);
		Assert.Contains("Longer than 10", r[5].Error);
		Assert.Equal("Refused", r[6].Error);
		Assert.Equal("Unknown tag", r[99].Error);

		var overflow = s.WriteAsync(new (int, object?)[] { (1, 40000.0) }, TimeSpan.FromSeconds(5));
		var o = await overflow;
		Assert.Contains("Out of range", o[1].Error);
	}

	[Fact]
	public async Task ScalingAppliesBothWays()
	{
		using var rig = new Rig();
		rig.Driver.Values["A"] = 16383.5;
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[] { Rig.Device() },
			new[] { Rig.Tag("a", "A", scale: new Scaling(0, 32767, 0, 100)) });
		var snap = s.Subscribe(new[] { c[0].Handle }, 100);

		Assert.Equal(Quality.Bad, snap[0].Value.Quality);
		Assert.Equal(Status.Waiting, snap[0].Value.Status);

		rig.Step();
		var changes = s.DrainChanges();
		Assert.Equal(50.0, (double)changes.Single().Value.Value!, 6);

		var w = s.WriteAsync(new (int, object?)[] { (1, 25.0) }, TimeSpan.FromSeconds(5));
		rig.Step();
		await w;
		Assert.Equal(8191.75, (double)rig.Driver.Values["A"]!, 6);
	}

	[Fact]
	public void DeadbandSuppressesSmallChangesButNotQuality()
	{
		using var rig = new Rig();
		rig.Driver.Values["A"] = 10.0;
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A", deadband: 0.5) });
		s.Subscribe(new[] { c[0].Handle }, 100);
		rig.Step();
		s.DrainChanges();

		rig.Driver.Values["A"] = 10.3;
		rig.Step(100);
		Assert.Empty(s.DrainChanges());

		rig.Driver.Values["A"] = 10.6;
		rig.Step(100);
		Assert.Equal(10.6, s.DrainChanges().Single().Value.Value);

		rig.Driver.FailRead = true;
		rig.Step(100);
		var bad = s.DrainChanges().Single().Value;
		Assert.Equal(Quality.Bad, bad.Quality);
		Assert.Equal(Status.Comm, bad.Status);
	}

	[Fact]
	public void ChangesMergeLatestValueWins()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		s.Subscribe(new[] { c[0].Handle }, 100);
		int signals = 0;
		s.ChangesAvailable = () => signals++;

		for (int i = 1; i <= 5; i++)
		{
			rig.Driver.Values["A"] = (double)i;
			rig.Step(100);
		}

		Assert.Equal(5, signals);
		var drained = s.DrainChanges();
		Assert.Single(drained);
		Assert.Equal(5.0, drained[0].Value.Value);
	}

	[Fact]
	public void ConnectionLossMarksEverythingBadThenRecovers()
	{
		using var rig = new Rig();
		rig.Driver.Values["A"] = 1.0;
		rig.Driver.Values["B"] = 2.0;
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A"), Rig.Tag("b", "B") });
		s.Subscribe(new[] { 1, 2 }, 100);
		rig.Step();
		s.DrainChanges();

		rig.Driver.FailRead = true;
		rig.Step(100);

		var bad = s.DrainChanges();
		Assert.Equal(2, bad.Count);
		Assert.All(bad, x => Assert.Equal(Status.Comm, x.Value.Status));
		Assert.Equal(DeviceState.Backoff, rig.Worker.State);
		Assert.True(rig.Driver.Connections[0].Disposed);

		rig.Driver.FailRead = false;
		rig.Step(100);
		Assert.Single(rig.Driver.Connections);

		rig.Step(400);
		Assert.Equal(2, rig.Driver.Connections.Count);
		Assert.Equal(DeviceState.Connected, rig.Worker.State);
		Assert.All(s.DrainChanges(), x => Assert.Equal(Quality.Good, x.Value.Quality));
		Assert.Equal(1, rig.Worker.Reconnects);
	}

	[Fact]
	public void BackoffGrowsAndIsCapped()
	{
		using var rig = new Rig();
		rig.Driver.FailConnect = true;
		var s = rig.Engine.CreateSession();
		s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		s.Subscribe(new[] { 1 }, 100);

		var waits = new List<long>();

		for (int i = 0; i < 9; i++)
		{
			long wait = rig.Worker.Step();
			waits.Add(wait);
			rig.Now += wait;
		}

		Assert.Equal(new long[] { 500, 1000, 2000, 4000, 8000, 15000, 30000, 30000, 30000 }, waits);
		Assert.Equal(9, rig.Driver.Connects);
		Assert.Equal("connection refused", rig.Worker.LastError);
	}

	[Fact]
	public async Task WritesFailFastWhileDisconnected()
	{
		using var rig = new Rig();
		rig.Driver.FailConnect = true;
		var s = rig.Engine.CreateSession();
		s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });

		var w = s.WriteAsync(new (int, object?)[] { (1, 1.0) }, TimeSpan.FromSeconds(5));
		rig.Step();
		var r = await w;

		Assert.False(r[1].Ok);
		Assert.Contains("not connected", r[1].Error);
	}

	[Fact]
	public void IdleConnectionIsClosed()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		s.Subscribe(new[] { 1 }, 100);
		rig.Step();

		s.Unsubscribe();
		rig.Step(1000);
		Assert.False(rig.Driver.Connections[0].Disposed);

		rig.Step(DeviceWorker.IdleDisconnectMs);
		Assert.True(rig.Driver.Connections[0].Disposed);
		Assert.Equal(DeviceState.Idle, rig.Worker.State);
	}

	[Fact]
	public void DisabledDeviceIsNeverContacted()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		var dev = Rig.Device() with { Enabled = false };
		s.Configure(new[] { dev }, new[] { Rig.Tag("a", "A") });
		var snap = s.Subscribe(new[] { 1 }, 100);

		rig.Step();

		Assert.Equal(0, rig.Driver.Connects);
		Assert.Equal(Status.Disabled, snap[0].Value.Status);
		Assert.Equal(DeviceState.Disabled, rig.Worker.State);
	}

	[Fact]
	public void DeviceRefusingAPointMarksOnlyThatPoint()
	{
		using var rig = new Rig();
		rig.Driver.Values["A"] = 1.0;
		var s = rig.Engine.CreateSession();
		s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A"), Rig.Tag("x", "BAD:X") });
		s.Subscribe(new[] { 1, 2 }, 100);
		rig.Step();

		var changes = s.DrainChanges().ToDictionary(x => x.Handle, x => x.Value);
		Assert.Equal(Quality.Good, changes[1].Quality);
		Assert.Equal(Status.Device, changes[2].Status);
		Assert.Equal("Illegal address", changes[2].Error);
		Assert.Equal(DeviceState.Connected, rig.Worker.State);
	}

	[Fact]
	public void ConfigErrorsStillGetHandles()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		var c = s.Configure(new[]
		{
			Rig.Device(),
			new DeviceConfig { Name = "NoHost", Protocol = "fake" },
			new DeviceConfig { Name = "Weird", Protocol = "carrier-pigeon", Host = "x" }
		}, new[]
		{
			Rig.Tag("ok", "A"),
			Rig.Tag("nodev", "A", "Missing"),
			Rig.Tag("badaddr", "has space"),
			Rig.Tag("nohost", "A", "NoHost"),
			Rig.Tag("proto", "A", "Weird"),
			Rig.Tag("ok", "B")
		});

		Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, c.Select(x => x.Handle));
		Assert.Null(c[0].Error);
		Assert.Equal("A", c[0].Normalized);
		Assert.Equal("Unknown device 'Missing'", c[1].Error);
		Assert.Equal("Bad address", c[2].Error);
		Assert.Equal("Host is required", c[3].Error);
		Assert.Equal("Unknown protocol 'carrier-pigeon'", c[4].Error);
		Assert.Equal("Tag 'ok' is defined twice", c[5].Error);

		var snap = s.Subscribe(c.Select(x => x.Handle).ToList(), 100);
		Assert.Equal(Status.Config, snap[1].Value.Status);
	}

	[Fact]
	public void ReconfigureReleasesOldPointsAndWorkers()
	{
		using var rig = new Rig();
		var s = rig.Engine.CreateSession();
		s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A"), Rig.Tag("b", "B") });
		var worker = rig.Worker;
		Assert.Equal(2, worker.PointCount);

		s.Subscribe(new[] { 1 }, 100);
		rig.Step();
		var pointValue = s.Snapshot(new[] { 1 })[0].Value;

		s.Configure(new[] { Rig.Device() }, new[] { Rig.Tag("a", "A") });
		Assert.Same(worker, rig.Worker);
		Assert.Equal(1, worker.PointCount);
		Assert.False(rig.Driver.Connections.Single().Disposed);
		Assert.Equal(pointValue, s.Snapshot(new[] { 1 })[0].Value);

		// Still hears about the shared point after the reconfigure.
		s.Subscribe(new[] { 1 }, 100);
		rig.Driver.Values["A"] = 77.0;
		rig.Step(100);
		Assert.Equal(77.0, s.DrainChanges().Single().Value.Value);

		s.Configure(Array.Empty<DeviceConfig>(), Array.Empty<TagConfig>());
		Assert.Empty(rig.Engine.Workers);
	}

	[Fact]
	public void StatusChangesReachSessionsUnderTheirOwnNames()
	{
		using var rig = new Rig();
		rig.Driver.FailConnect = true;
		var s1 = rig.Engine.CreateSession();
		var s2 = rig.Engine.CreateSession();
		s1.Configure(new[] { Rig.Device("Line1") }, new[] { Rig.Tag("a", "A", "Line1") });
		s2.Configure(new[] { Rig.Device("Mixer") }, new[] { Rig.Tag("a", "A", "Mixer") });
		var seen1 = new List<DeviceStatus>();
		var seen2 = new List<DeviceStatus>();
		s1.StatusChanged = l => seen1.AddRange(l);
		s2.StatusChanged = l => seen2.AddRange(l);
		s1.Subscribe(new[] { 1 }, 100);

		rig.Step();

		Assert.Contains(seen1, x => x.Name == "Line1" && x.State == DeviceState.Backoff &&
			x.LastError == "connection refused");
		Assert.Contains(seen2, x => x.Name == "Mixer" && x.State == DeviceState.Backoff);
	}

	[Fact]
	public void ValidateWithoutConfiguring()
	{
		using var rig = new Rig();
		var r = rig.Engine.Validate("fake", new[] { "A", "bad one" });

		Assert.Equal("A", r[0].Address!.Normalized);
		Assert.Equal("Bad address", r[1].Error);
		Assert.Contains("Unknown protocol", rig.Engine.Validate("nope", new[] { "A" })[0].Error);
	}
}
