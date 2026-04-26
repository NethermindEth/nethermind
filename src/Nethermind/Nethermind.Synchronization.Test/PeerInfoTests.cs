// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [TestFixture(AllocationContexts.Blocks)]
    [TestFixture(AllocationContexts.Receipts)]
    [TestFixture(AllocationContexts.Headers)]
    [TestFixture(AllocationContexts.Bodies)]
    [TestFixture(AllocationContexts.State)]
    [TestFixture(AllocationContexts.All)]
    [Parallelizable(ParallelScope.All)]
    public class PeerInfoTests(AllocationContexts contexts)
    {
        private readonly AllocationContexts _contexts = contexts;

        [Test]
        public void Can_put_to_sleep_by_contexts()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            for (int i = 0; i < PeerInfo.SleepThreshold - 1; i++)
            {
                AllocationContexts sleeps = peerInfo.IncreaseWeakness(_contexts);
                sleeps.Should().Be(AllocationContexts.None);
            }

            AllocationContexts sleeps2 = peerInfo.IncreaseWeakness(_contexts);
            sleeps2.Should().Be(_contexts);
        }

        [Test]
        public void Can_put_to_sleep()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            for (int i = 0; i < PeerInfo.SleepThreshold - 1; i++)
            {
                AllocationContexts sleeps = peerInfo.IncreaseWeakness(_contexts);
                sleeps.Should().Be(AllocationContexts.None);
            }

            AllocationContexts sleeps2 = peerInfo.IncreaseWeakness(_contexts);
            sleeps2.Should().Be(_contexts);
            peerInfo.PutToSleep(sleeps2, DateTime.MinValue);
            peerInfo.IsAsleep(_contexts).Should().BeTrue();
        }

        [Test]
        public void Can_wake_up()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            for (int i = 0; i < PeerInfo.SleepThreshold - 1; i++)
            {
                AllocationContexts sleeps = peerInfo.IncreaseWeakness(_contexts);
                sleeps.Should().Be(AllocationContexts.None);
            }

            AllocationContexts sleeps2 = peerInfo.IncreaseWeakness(_contexts);
            sleeps2.Should().Be(_contexts);
            peerInfo.PutToSleep(sleeps2, DateTime.MinValue);
            peerInfo.IsAsleep(_contexts).Should().BeTrue();
            peerInfo.TryToWakeUp(DateTime.MinValue, TimeSpan.Zero);
            peerInfo.IsAsleep(_contexts).Should().BeFalse();
        }

        [Test]
        public void Can_fail_to_wake_up()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            for (int i = 0; i < PeerInfo.SleepThreshold - 1; i++)
            {
                AllocationContexts sleeps = peerInfo.IncreaseWeakness(_contexts);
                sleeps.Should().Be(AllocationContexts.None);
            }

            AllocationContexts sleeps2 = peerInfo.IncreaseWeakness(_contexts);
            sleeps2.Should().Be(_contexts);
            peerInfo.PutToSleep(sleeps2, DateTime.MinValue);
            peerInfo.IsAsleep(_contexts).Should().BeTrue();
            peerInfo.TryToWakeUp(DateTime.MinValue.Add(TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(3));
            peerInfo.IsAsleep(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();
        }

        [Test]
        public void Can_allocate()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.IsAllocated(_contexts).Should().BeFalse();
            peerInfo.TryAllocate(_contexts);
            peerInfo.IsAllocated(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();
        }

        [Test]
        public void Can_free()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.IsAllocated(_contexts).Should().BeFalse();
            peerInfo.TryAllocate(_contexts);
            peerInfo.IsAllocated(_contexts).Should().BeTrue();
            peerInfo.Free(_contexts);
            peerInfo.IsAllocated(_contexts).Should().BeFalse();
            peerInfo.CanBeAllocated(_contexts).Should().BeTrue();
        }

        [Test]
        public void Cannot_allocate_subcontext()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.TryAllocate(AllocationContexts.Blocks);
            peerInfo.IsAllocated(AllocationContexts.Bodies).Should().BeTrue();
            peerInfo.IsAllocated(AllocationContexts.Headers).Should().BeFalse();
            peerInfo.IsAllocated(AllocationContexts.Receipts).Should().BeTrue();
            peerInfo.CanBeAllocated(AllocationContexts.Bodies).Should().BeFalse();
            peerInfo.CanBeAllocated(AllocationContexts.Headers).Should().BeTrue();
            peerInfo.CanBeAllocated(AllocationContexts.Receipts).Should().BeFalse();

            peerInfo.Free(AllocationContexts.Receipts);
            peerInfo.IsAllocated(AllocationContexts.Receipts).Should().BeFalse();
            peerInfo.IsAllocated(AllocationContexts.Bodies).Should().BeTrue();
        }

        [Test]
        public void Cannot_allocate_subcontext_of_sleeping()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.PutToSleep(AllocationContexts.Blocks, DateTime.MinValue);
            peerInfo.CanBeAllocated(AllocationContexts.Bodies).Should().BeFalse();
        }

        [Test]
        public void Free_does_not_set_unallocated_context()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.TryAllocate(AllocationContexts.Headers);

            peerInfo.Free(AllocationContexts.Bodies);

            peerInfo.IsAllocated(AllocationContexts.Headers).Should().BeTrue();
            peerInfo.IsAllocated(AllocationContexts.Bodies).Should().BeFalse();
        }

        [Test]
        public void WakeUp_does_not_put_awake_context_to_sleep()
        {
            DateTime t0 = DateTime.UtcNow;
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());

            peerInfo.PutToSleep(AllocationContexts.Blocks, t0);
            peerInfo.PutToSleep(AllocationContexts.Bodies, t0 - TimeSpan.FromSeconds(10));

            peerInfo.TryToWakeUp(t0, TimeSpan.FromSeconds(5));

            peerInfo.TryToWakeUp(t0 + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

            peerInfo.IsAsleep(AllocationContexts.Bodies).Should().BeFalse();
            peerInfo.IsAsleep(AllocationContexts.Receipts).Should().BeFalse();
        }

        [Test]
        public void Can_allocate_multiple()
        {
            if (!PeerInfo.IsOnlyOneContext(_contexts) || _contexts == AllocationContexts.None) return;

            AllocationAllowances allowances = AllocationAllowances.Default;
            allowances[_contexts] = 5;

            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>(), allowances);

            for (int i = 0; i < 5; i++)
            {
                peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
                peerInfo.TryAllocate(_contexts).Should().BeTrue();
            }
            peerInfo.IsAllocationFull(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();

            peerInfo.Free(_contexts);
            peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
            peerInfo.TryAllocate(_contexts).Should().BeTrue();
            peerInfo.IsAllocationFull(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();
        }

        [Test]
        public void Free_does_not_exceed_allowance()
        {
            // Multi-slot Free guards: calling Free without a prior TryAllocate must not push the slot above its allowance.
            AllocationAllowances allowances = new(headers: 2, bodies: 1, receipts: 1, state: 1, snap: 1, forwardHeader: 1);
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>(), allowances);

            peerInfo.AvailableAllocationSlots.Headers.Should().Be(2);
            peerInfo.Free(AllocationContexts.Headers);
            peerInfo.AvailableAllocationSlots.Headers.Should().Be(2);

            peerInfo.TryAllocate(AllocationContexts.Headers).Should().BeTrue();
            peerInfo.AvailableAllocationSlots.Headers.Should().Be(1);
            peerInfo.Free(AllocationContexts.Headers);
            peerInfo.AvailableAllocationSlots.Headers.Should().Be(2);
            peerInfo.Free(AllocationContexts.Headers);
            peerInfo.AvailableAllocationSlots.Headers.Should().Be(2, "Free should not exceed the configured allowance");
        }

        [Test]
        public void TryAllocate_rolls_back_on_partial_failure()
        {
            // Take Bodies first, then attempt Blocks (= Bodies | Receipts) which will fail because Bodies is full.
            // The Receipts slot must not be left decremented.
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.TryAllocate(AllocationContexts.Bodies).Should().BeTrue();

            peerInfo.TryAllocate(AllocationContexts.Blocks).Should().BeFalse();

            peerInfo.AvailableAllocationSlots.Bodies.Should().Be(0);
            peerInfo.AvailableAllocationSlots.Receipts.Should().Be(1, "rollback must restore the partially-claimed slot");
            peerInfo.IsAllocationFull(AllocationContexts.Receipts).Should().BeFalse();
        }

        [Test]
        public void Concurrent_TryAllocate_does_not_oversubscribe()
        {
            // Many threads racing against a fixed slot count: total successful allocations must equal the allowance,
            // proving the lock-free CAS loop in TryAllocate does not lose or duplicate slots under contention.
            const byte slots = 8;
            const int threads = 32;
            AllocationAllowances allowances = new(headers: slots, bodies: 1, receipts: 1, state: 1, snap: 1, forwardHeader: 1);
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>(), allowances);
            int succeeded = 0;
            using Barrier barrier = new(threads);

            Task[] tasks = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    if (peerInfo.TryAllocate(AllocationContexts.Headers))
                        Interlocked.Increment(ref succeeded);
                });
            }
            Task.WaitAll(tasks);

            succeeded.Should().Be(slots);
            peerInfo.AvailableAllocationSlots.Headers.Should().Be(0);
            peerInfo.IsAllocationFull(AllocationContexts.Headers).Should().BeTrue();
        }
    }
}
