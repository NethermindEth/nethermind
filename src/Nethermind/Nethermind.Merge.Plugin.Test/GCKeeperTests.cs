// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.GC;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

// The keeper holds the process-wide GCScheduler guard while a payload is active.
[NonParallelizable]
public class GCKeeperTests
{
    private const int WaitMs = 5_000;
    private const int PollMs = 10;

    private sealed class FakeGCRuntime : IGCRuntime
    {
        // Set while the runtime would be waiting on an in-flight background collection.
        public readonly ManualResetEventSlim Blocked = new(initialState: false);
        private int _startCalls;
        private int _endCalls;
        private volatile bool _inRegion;

        public int StartCalls => Volatile.Read(ref _startCalls);
        public int EndCalls => Volatile.Read(ref _endCalls);
        public bool IsInNoGCRegion => _inRegion;

        public bool TryStartNoGCRegion(long totalSize, long lohSize)
        {
            Interlocked.Increment(ref _startCalls);
            while (Blocked.IsSet) Thread.Sleep(1);
            _inRegion = true;
            return true;
        }

        public void EndNoGCRegion()
        {
            Interlocked.Increment(ref _endCalls);
            _inRegion = false;
        }
    }

    private static (GCKeeper keeper, FakeGCRuntime runtime) CreateKeeper()
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        FakeGCRuntime runtime = new();
        return (new GCKeeper(strategy, LimboLogs.Instance, runtime), runtime);
    }

    [Test]
    public void Region_starts_and_ends_with_the_payload_when_the_start_is_prompt()
    {
        (GCKeeper keeper, FakeGCRuntime runtime) = CreateKeeper();
        using (keeper)
        {
            using (keeper.TryStartNoGCRegion())
            {
                Assert.That(runtime.IsInNoGCRegion, Is.True);
            }

            Assert.That(runtime.EndCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Payload_does_not_wait_for_a_blocked_start_and_adopts_the_region_when_it_lands()
    {
        (GCKeeper keeper, FakeGCRuntime runtime) = CreateKeeper();
        using (keeper)
        {
            runtime.Blocked.Set();
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (keeper.TryStartNoGCRegion())
            {
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
                Assert.That(runtime.IsInNoGCRegion, Is.False);

                runtime.Blocked.Reset();
                Assert.That(() => runtime.IsInNoGCRegion, Is.True.After(WaitMs, PollMs));
            }

            Assert.That(runtime.EndCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Region_landing_after_the_payload_finished_is_ended_by_the_keeper()
    {
        (GCKeeper keeper, FakeGCRuntime runtime) = CreateKeeper();
        using (keeper)
        {
            runtime.Blocked.Set();
            keeper.TryStartNoGCRegion().Dispose();
            Assert.That(runtime.EndCalls, Is.Zero);

            runtime.Blocked.Reset();
            Assert.That(() => runtime.EndCalls, Is.EqualTo(1).After(WaitMs, PollMs));
            Assert.That(runtime.IsInNoGCRegion, Is.False);
        }
    }

    [Test]
    public void Next_payload_reuses_the_pending_start_instead_of_queueing_another()
    {
        (GCKeeper keeper, FakeGCRuntime runtime) = CreateKeeper();
        using (keeper)
        {
            runtime.Blocked.Set();
            keeper.TryStartNoGCRegion().Dispose();
            using (keeper.TryStartNoGCRegion())
            {
                Assert.That(runtime.StartCalls, Is.EqualTo(1));

                runtime.Blocked.Reset();
                Assert.That(() => runtime.IsInNoGCRegion, Is.True.After(WaitMs, PollMs));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(runtime.StartCalls, Is.EqualTo(1));
                Assert.That(runtime.EndCalls, Is.EqualTo(1));
            }
        }
    }
}
