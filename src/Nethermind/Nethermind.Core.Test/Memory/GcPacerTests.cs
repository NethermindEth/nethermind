// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test.Memory;

// The scheduler gate (MarkGCPaused) and the GC latency mode are process-wide, hence NonParallelizable.
[NonParallelizable]
public class GcPacerTests
{
    private const long MiB = 1024 * 1024;

    public enum Gate
    {
        PruningExclusion,
        PayloadPause,
        NoGCRegion,
    }

    [SetUp]
    public void SetUp() => GCScheduler.Instance.SweepBaselineAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);

    [TestCase(false, 16 * MiB, ExpectedResult = true)]
    [TestCase(false, 16 * MiB - 1, ExpectedResult = false)]
    [TestCase(true, 64 * MiB, ExpectedResult = false)]
    public bool IsGen0Due(bool collectedSinceLastTick, long allocatedSinceLastTick) =>
        GcPacer.IsGen0Due(collectedSinceLastTick, allocatedSinceLastTick);

    [TestCase(60_000, 60_000, false, false, ExpectedResult = true)]
    [TestCase(59_999, 60_000, false, false, ExpectedResult = false)]
    [TestCase(60_000, 60_000, false, true, ExpectedResult = false)]
    [TestCase(15_000, 60_000, true, false, ExpectedResult = true)]
    [TestCase(14_999, 60_000, true, false, ExpectedResult = false)]
    [TestCase(5_000, 8_000, true, false, ExpectedResult = true)]
    [TestCase(4_999, 8_000, true, false, ExpectedResult = false)]
    public bool IsGen2Due(long sinceLastGen2Ms, long gen2IntervalMs, bool warmup, bool backgroundGcPending) =>
        GcPacer.IsGen2Due(sinceLastGen2Ms, gen2IntervalMs, warmup, backgroundGcPending);

    [TestCase(5, 6, 0, ExpectedResult = true)]
    [TestCase(5, 5, 179_999, ExpectedResult = false)]
    [TestCase(5, 5, 180_000, ExpectedResult = true)]
    public bool IsBackgroundGcSettled(long pendingSinceIndex, long backgroundIndex, long pendingForMs) =>
        GcPacer.IsBackgroundGcSettled(pendingSinceIndex, backgroundIndex, pendingForMs);

    [TestCase(10_000, false, ExpectedResult = 10_000)]
    [TestCase(10_000, true, ExpectedResult = 5_000)]
    [TestCase(1_500, true, ExpectedResult = 1_000)]
    [TestCase(500, true, ExpectedResult = 500)]
    public long Gen1IntervalMs(long gen1IntervalMs, bool warmup) => GcPacer.Gen1IntervalMs(gen1IntervalMs, warmup);

    [TestCase(60_000, false, ExpectedResult = 60_000)]
    [TestCase(60_000, true, ExpectedResult = 15_000)]
    [TestCase(8_000, true, ExpectedResult = 5_000)]
    [TestCase(2_000, true, ExpectedResult = 2_000)]
    public long Gen2IntervalMs(long gen2IntervalMs, bool warmup) => GcPacer.Gen2IntervalMs(gen2IntervalMs, warmup);

    [TestCase(-5, ExpectedResult = 1)]
    [TestCase(0, ExpectedResult = 1)]
    [TestCase(250, ExpectedResult = 250)]
    [TestCase(long.MaxValue, ExpectedResult = int.MaxValue)]
    public long ClampIntervalMs(long intervalMs) => GcPacer.ClampIntervalMs(intervalMs);

    [TestCase(Gate.PruningExclusion, 0, ExpectedResult = false)]
    [TestCase(Gate.PayloadPause, 0, ExpectedResult = true)]
    [TestCase(Gate.NoGCRegion, 0, ExpectedResult = false)]
    [TestCase(Gate.PruningExclusion, 1, ExpectedResult = false)]
    [TestCase(Gate.PayloadPause, 1, ExpectedResult = false)]
    [TestCase(Gate.NoGCRegion, 1, ExpectedResult = false)]
    [TestCase(Gate.PruningExclusion, 2, ExpectedResult = false)]
    [TestCase(Gate.PayloadPause, 2, ExpectedResult = false)]
    [TestCase(Gate.NoGCRegion, 2, ExpectedResult = false)]
    public bool Paced_collection_respects_gate(Gate gate, int generation)
    {
        GCScheduler scheduler = new(sustainedSweepEnabled: false);
        using GcPacer pacer = CreatePacer(scheduler);
        using GCScheduler.ForcedGCExclusionScope? exclusion = gate == Gate.PruningExclusion ? scheduler.ExcludeForcedGC() : null;
        bool paused = gate == Gate.PayloadPause && GCScheduler.MarkGCPaused();
        bool inRegion = gate == Gate.NoGCRegion && GC.TryStartNoGCRegion(64 * MiB);
        try
        {
            Assert.That(paused || inRegion || gate == Gate.PruningExclusion, Is.True);
            return generation == 0 ? pacer.TryCollectGen0() : pacer.CollectThroughScheduler(generation);
        }
        finally
        {
            if (paused) GCScheduler.MarkGCResumed();
            if (inRegion && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }
    }

    [Test]
    public void Collections_run_when_no_gate_is_held([Values(0, 1, 2)] int generation)
    {
        using GcPacer pacer = CreatePacer(new GCScheduler(sustainedSweepEnabled: false));
        Assert.That(generation == 0 ? pacer.TryCollectGen0() : pacer.CollectThroughScheduler(generation), Is.True);
    }

    [TestCase(0, 0, ExpectedResult = false)]
    [TestCase(-1, -1, ExpectedResult = false)]
    [TestCase(50, 0, ExpectedResult = true)]
    [TestCase(0, 1_000, ExpectedResult = true)]
    public bool TryStart_first_call_wins_unless_disabled(long gen0IntervalMs, long gen1IntervalMs)
    {
        using GcPacer pacer = new(gen0IntervalMs, gen1IntervalMs, 0, 0, LimboLogs.Instance, new GCScheduler(sustainedSweepEnabled: false));
        bool started = pacer.TryStart();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pacer.IsRunning, Is.EqualTo(started));
            Assert.That(pacer.TryStart(), Is.False);
        }
        return started;
    }

    [Test]
    public void Dispose_stops_the_pacer_and_TryStart_after_dispose_returns_false()
    {
        GcPacer started = CreatePacer(new GCScheduler(sustainedSweepEnabled: false));
        Assert.That(started.TryStart(), Is.True);
        started.Dispose();

        GcPacer neverStarted = CreatePacer(new GCScheduler(sustainedSweepEnabled: false));
        neverStarted.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(started.IsRunning, Is.False);
            Assert.That(neverStarted.TryStart(), Is.False);
        }
    }

    private static GcPacer CreatePacer(GCScheduler scheduler) => new(50, 1_000, 0, 0, LimboLogs.Instance, scheduler);
}
