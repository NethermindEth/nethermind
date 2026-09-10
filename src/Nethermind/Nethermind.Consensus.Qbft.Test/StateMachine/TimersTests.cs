// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.StateMachine;

/// <summary>Port of Besu's <c>RoundTimerTest</c>, <c>BlockTimerTest</c> and <c>BftRoundExpiryTimeCalculatorTest</c>.</summary>
[Parallelizable(ParallelScope.All)]
public class TimersTests
{
    private sealed class RecordingScheduler : IBftTimerScheduler
    {
        public List<(Action Callback, TimeSpan Delay)> Scheduled { get; } = [];
        public int Cancelled { get; private set; }

        public IDisposable Schedule(Action callback, TimeSpan delay)
        {
            Scheduled.Add((callback, delay));
            return new Handle(this);
        }

        private sealed class Handle(RecordingScheduler owner) : IDisposable
        {
            public void Dispose() => owner.Cancelled++;
        }
    }

    private sealed class RecordingQueue : IBftEventQueue
    {
        public List<BftEvent> Events { get; } = [];
        public void Add(BftEvent bftEvent) => Events.Add(bftEvent);
    }

    [TestCase(0, 1000)]
    [TestCase(1, 2000)]
    [TestCase(2, 4000)]
    [TestCase(5, 32000)]
    public void RoundExpiryDoublesEachRound(int round, int expectedMillis) =>
        Assert.That(new BftRoundExpiryTimeCalculator(TimeSpan.FromSeconds(1)).CalculateRoundExpiry(new ConsensusRoundIdentifier(1, round)), Is.EqualTo(TimeSpan.FromMilliseconds(expectedMillis)));

    [TestCase(40)]
    [TestCase(1000)]
    public void RoundExpirySaturatesInsteadOfOverflowing(int round) =>
        Assert.That(new BftRoundExpiryTimeCalculator(TimeSpan.FromSeconds(1)).CalculateRoundExpiry(new ConsensusRoundIdentifier(1, round)), Is.EqualTo(BftRoundExpiryTimeCalculator.MaxExpiry));

    [Test]
    public void RoundTimerSchedulesExpiryEventAndCancelsPrevious()
    {
        RecordingScheduler scheduler = new();
        RecordingQueue queue = new();
        RoundTimer timer = new(queue, new BftRoundExpiryTimeCalculator(TimeSpan.FromSeconds(1)), scheduler, LimboLogs.Instance);
        ConsensusRoundIdentifier round = new(1, 1);

        timer.StartTimer(round);
        Assert.That(timer.IsRunning, Is.True);
        Assert.That(scheduler.Scheduled[0].Delay, Is.EqualTo(TimeSpan.FromSeconds(2)));

        timer.StartTimer(new ConsensusRoundIdentifier(1, 2));
        Assert.That(scheduler.Cancelled, Is.EqualTo(1));

        scheduler.Scheduled[1].Callback();
        Assert.That(queue.Events, Is.EqualTo(new BftEvent[] { new RoundExpiryEvent(new ConsensusRoundIdentifier(1, 2)) }));

        timer.CancelTimer();
        Assert.That(timer.IsRunning, Is.False);
    }

    private static QbftForksSchedule Schedule(int blockPeriodSeconds, int emptyBlockPeriodSeconds = 0, long xBlockPeriodMillis = 0) =>
        QbftForksSchedule.Create(new QbftChainSpecEngineParameters { BlockPeriodSeconds = blockPeriodSeconds, EmptyBlockPeriodSeconds = emptyBlockPeriodSeconds, XBlockPeriodMilliseconds = xBlockPeriodMillis }, ulong.MaxValue);

    [Test]
    public void BlockTimerExpiresAtParentTimestampPlusBlockPeriod()
    {
        RecordingScheduler scheduler = new();
        RecordingQueue queue = new();
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(1005));
        BlockTimer timer = new(queue, Schedule(5), scheduler, clock, LimboLogs.Instance);
        ConsensusRoundIdentifier round = new(1, 0);

        timer.StartTimer(round, 1002);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(timer.IsRunning, Is.True);
            Assert.That(scheduler.Scheduled[0].Delay, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(timer.BlockPeriodSeconds, Is.EqualTo(5));
        }

        scheduler.Scheduled[0].Callback();
        Assert.That(queue.Events, Is.EqualTo(new BftEvent[] { new BlockTimerExpiryEvent(round) }));
    }

    [Test]
    public void BlockTimerFiresImmediatelyWhenTheParentIsOldEnough()
    {
        RecordingScheduler scheduler = new();
        RecordingQueue queue = new();
        ConsensusRoundIdentifier round = new(1, 0);
        BlockTimer timer = new(queue, Schedule(1), scheduler, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(2000)), LimboLogs.Instance);
        timer.StartTimer(round, 1000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scheduler.Scheduled, Is.Empty);
            Assert.That(queue.Events, Is.EqualTo(new BftEvent[] { new BlockTimerExpiryEvent(round) }));
            Assert.That(timer.IsRunning, Is.False);
        }
    }

    [Test]
    public void BlockTimerHonoursTheLongerPeriodOfAnImminentFork()
    {
        RecordingScheduler scheduler = new();
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(1000));
        // Fork at timestamp 1001 to a 10s period; the block after a parent at 1000 must wait 10s, not 1s.
        QbftChainSpecEngineParameters parameters = new() { BlockPeriodSeconds = 1, Transitions = [new QbftTransition { Block = 1001, BlockPeriodSeconds = 10 }] };
        BlockTimer timer = new(new RecordingQueue(), QbftForksSchedule.Create(parameters, 1001), scheduler, clock, LimboLogs.Instance);
        timer.StartTimer(new ConsensusRoundIdentifier(1, 0), 1000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(timer.BlockPeriodSeconds, Is.EqualTo(10));
            Assert.That(scheduler.Scheduled[0].Delay, Is.EqualTo(TimeSpan.FromSeconds(10)));
        }
    }

    [Test]
    public void MillisecondTestPeriodCountsFromNow()
    {
        RecordingScheduler scheduler = new();
        BlockTimer timer = new(new RecordingQueue(), Schedule(1, xBlockPeriodMillis: 250), scheduler, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(5000)), LimboLogs.Instance);
        timer.StartTimer(new ConsensusRoundIdentifier(1, 0), 1000);
        Assert.That(scheduler.Scheduled[0].Delay, Is.EqualTo(TimeSpan.FromMilliseconds(250)));
    }

    [Test]
    public void EmptyBlockPeriodTracking()
    {
        RecordingScheduler scheduler = new();
        ManualTimestamper clock = new(DateTime.UnixEpoch.AddSeconds(1000));
        BlockTimer timer = new(new RecordingQueue(), Schedule(1, emptyBlockPeriodSeconds: 60), scheduler, clock, LimboLogs.Instance);
        ConsensusRoundIdentifier round = new(1, 0);
        timer.StartTimer(round, 1000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scheduler.Scheduled[0].Delay, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(timer.EmptyBlockPeriodSeconds, Is.EqualTo(60));
            Assert.That(timer.CheckEmptyBlockExpired(1000, 1001_000), Is.False);
            Assert.That(timer.CheckEmptyBlockExpired(1000, 1060_001), Is.True);
            Assert.That(timer.EmptyBlockWaitSeconds, Is.EqualTo(0));
        }

        clock.Add(TimeSpan.FromSeconds(1));
        timer.ResetTimerForEmptyBlock(round, 1000, 1001_000);
        clock.Add(TimeSpan.FromSeconds(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scheduler.Scheduled[1].Delay, Is.EqualTo(TimeSpan.FromSeconds(1)), "re-armed for one more block period, sooner than the empty period end");
            Assert.That(timer.EmptyBlockPeriodExpiryMillis, Is.EqualTo(1060_000));
            Assert.That(timer.EmptyBlockWaitSeconds, Is.EqualTo(3));
        }

        timer.ResetTimerForEmptyBlock(round, 1000, 1059_500);
        Assert.That(scheduler.Scheduled[2].Delay, Is.EqualTo(TimeSpan.FromSeconds(56)), "capped at the empty period end (1060s) rather than one more block period (1060.5s)");
    }
}
