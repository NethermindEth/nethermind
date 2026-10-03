// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Paced collections share GCScheduler's process-wide static guard with GCSchedulerTests.
[NonParallelizable]
public class GcPacerTests
{
    private const long IdleIntervalMs = 600_000;

    [SetUp]
    public void SetUp() => GCScheduler.Instance.SweepBaselineAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);

    private static GcPacer CreatePacer(long gen0IntervalMs = 0, long gen1IntervalMs = 0, GCScheduler? scheduler = null) =>
        new(gen0IntervalMs, gen1IntervalMs, gen2IntervalMs: 0, warmupSeconds: 0, LimboLogs.Instance,
            scheduler ?? new GCScheduler(sustainedSweepEnabled: false));

    [TestCase(0L, 0L, false, TestName = "TryStart_PacingDisabled_DoesNotStart")]
    [TestCase(0L, IdleIntervalMs, true, TestName = "TryStart_Gen1CadenceOnly_Starts")]
    [TestCase(IdleIntervalMs, 0L, true, TestName = "TryStart_Gen0CadenceOnly_Starts")]
    public void TryStart_FollowsConfiguredCadence(long gen0IntervalMs, long gen1IntervalMs, bool expectedStarted)
    {
        using GcPacer pacer = CreatePacer(gen0IntervalMs, gen1IntervalMs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pacer.TryStart(), Is.EqualTo(expectedStarted));
            Assert.That(pacer.IsRunning, Is.EqualTo(expectedStarted));
        }
    }

    [Test]
    public void TryStart_SecondCall_IsNoOp()
    {
        using GcPacer pacer = CreatePacer(gen1IntervalMs: IdleIntervalMs);

        pacer.TryStart();

        Assert.That(pacer.TryStart(), Is.False);
    }

    [Test]
    public void Dispose_JoinsPacerThreads()
    {
        GcPacer pacer = CreatePacer(IdleIntervalMs, IdleIntervalMs);
        pacer.TryStart();

        pacer.Dispose();

        Assert.That(pacer.IsRunning, Is.False);
    }

    [Test]
    public void Dispose_IsIdempotent()
    {
        GcPacer pacer = CreatePacer(gen1IntervalMs: IdleIntervalMs);
        pacer.TryStart();

        pacer.Dispose();

        Assert.DoesNotThrow(pacer.Dispose);
    }

    [TestCase(1L, ExpectedResult = 1L)]
    [TestCase(0L, ExpectedResult = 1L)]
    [TestCase(long.MaxValue, ExpectedResult = int.MaxValue)]
    public long ClampIntervalMs_BoundsToWaitRange(long intervalMs) => GcPacer.ClampIntervalMs(intervalMs);

    [TestCase(10_000L, false, ExpectedResult = 10_000L)]
    [TestCase(10_000L, true, ExpectedResult = 5_000L)]
    [TestCase(1_500L, true, ExpectedResult = 1_000L)]
    public long Gen1IntervalMs_HalvesDuringWarmupWithFloor(long gen1IntervalMs, bool warmup) =>
        GcPacer.Gen1IntervalMs(gen1IntervalMs, warmup);

    [TestCase(60_000L, false, ExpectedResult = 60_000L)]
    [TestCase(60_000L, true, ExpectedResult = 15_000L)]
    [TestCase(8_000L, true, ExpectedResult = 5_000L)]
    public long Gen2IntervalMs_QuartersDuringWarmupWithFloor(long gen2IntervalMs, bool warmup) =>
        GcPacer.Gen2IntervalMs(gen2IntervalMs, warmup);

    [TestCase(false, 0L, ExpectedResult = false, TestName = "IsGen0Due_IdleTick_Skips")]
    [TestCase(false, 16L * 1024 * 1024 - 1, ExpectedResult = false, TestName = "IsGen0Due_BelowAllocationThreshold_Skips")]
    [TestCase(false, 16L * 1024 * 1024, ExpectedResult = true, TestName = "IsGen0Due_AtAllocationThreshold_Collects")]
    [TestCase(true, 1L << 30, ExpectedResult = false, TestName = "IsGen0Due_RuntimeAlreadyCollected_Skips")]
    public bool IsGen0Due(bool collectedSinceLastTick, long allocatedSinceLastTick) =>
        GcPacer.IsGen0Due(collectedSinceLastTick, allocatedSinceLastTick);

    [TestCase(59_999L, false, false, ExpectedResult = false)]
    [TestCase(60_000L, false, false, ExpectedResult = true)]
    [TestCase(60_000L, false, true, ExpectedResult = false)]
    [TestCase(15_000L, true, false, ExpectedResult = true)]
    [TestCase(14_999L, true, false, ExpectedResult = false)]
    public bool IsGen2Due_WaitsForCadenceAndPendingBackgroundGc(long sinceLastGen2Ms, bool warmup, bool backgroundGcPending) =>
        GcPacer.IsGen2Due(sinceLastGen2Ms, 60_000, warmup, backgroundGcPending);

    [TestCase(5L, 6L, 0L, ExpectedResult = true, TestName = "IsBackgroundGcSettled_BackgroundGcRan")]
    [TestCase(5L, 5L, 179_999L, ExpectedResult = false, TestName = "IsBackgroundGcSettled_StillPending")]
    [TestCase(5L, 5L, 180_000L, ExpectedResult = true, TestName = "IsBackgroundGcSettled_BlockingFallbackTimesOut")]
    public bool IsBackgroundGcSettled(long pendingSinceIndex, long backgroundIndex, long pendingForMs) =>
        GcPacer.IsBackgroundGcSettled(pendingSinceIndex, backgroundIndex, pendingForMs);

    [Test]
    public void CollectThroughScheduler_Gen2_RestartsSustainedSweepBudget()
    {
        GCScheduler scheduler = new(sustainedSweepEnabled: false) { SweepBaselineAllocatedBytes = 0 };
        using GcPacer pacer = CreatePacer(scheduler: scheduler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pacer.CollectThroughScheduler(GC.MaxGeneration), Is.True);
            Assert.That(scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(0));
        }
    }

    [Test]
    public void CollectThroughScheduler_ForcedGCExcluded_Skips([Values(1, 2)] int generation)
    {
        GCScheduler scheduler = new(sustainedSweepEnabled: false) { SweepBaselineAllocatedBytes = 0 };
        using GcPacer pacer = CreatePacer(scheduler: scheduler);
        using GCScheduler.ForcedGCExclusionScope exclusion = scheduler.ExcludeForcedGC();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pacer.CollectThroughScheduler(generation), Is.False);
            Assert.That(scheduler.SweepBaselineAllocatedBytes, Is.Zero);
        }
    }
}
