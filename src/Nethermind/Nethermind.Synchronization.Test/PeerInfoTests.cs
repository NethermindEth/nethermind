// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
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
            peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
            peerInfo.TryAllocate(_contexts);
            peerInfo.IsAllocationFull(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();
        }

        [Test]
        public void Can_allocate_multiple()
        {
            if (!PeerInfo.IsOnlyOneContext(_contexts)) return;

            Dictionary<AllocationContexts, int> newAllowances = PeerInfo.DefaultAllowances.ToDictionary();
            newAllowances[_contexts] = 5;

            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>(), newAllowances.ToFrozenDictionary());

            for (int i = 0; i < 5; i++)
            {
                peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
                peerInfo.TryAllocate(_contexts);
            }
            peerInfo.IsAllocationFull(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();

            // Partial free
            peerInfo.Free(_contexts);
            peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
            peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
            peerInfo.TryAllocate(_contexts);
            peerInfo.IsAllocationFull(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();
        }

        [Test]
        public void Can_free()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
            peerInfo.TryAllocate(_contexts);
            peerInfo.IsAllocationFull(_contexts).Should().BeTrue();
            peerInfo.Free(_contexts);
            peerInfo.IsAllocationFull(_contexts).Should().BeFalse();
            peerInfo.CanBeAllocated(_contexts).Should().BeTrue();
        }

        [Test]
        public void Cannot_allocate_subcontext()
        {
            PeerInfo peerInfo = new(Substitute.For<ISyncPeer>());
            peerInfo.TryAllocate(AllocationContexts.Blocks);
            peerInfo.IsAllocationFull(AllocationContexts.Bodies).Should().BeTrue();
            peerInfo.IsAllocationFull(AllocationContexts.Headers).Should().BeFalse();
            peerInfo.IsAllocationFull(AllocationContexts.Receipts).Should().BeTrue();
            peerInfo.CanBeAllocated(AllocationContexts.Bodies).Should().BeFalse();
            peerInfo.CanBeAllocated(AllocationContexts.Headers).Should().BeTrue();
            peerInfo.CanBeAllocated(AllocationContexts.Receipts).Should().BeFalse();

            peerInfo.Free(AllocationContexts.Receipts);
            peerInfo.IsAllocationFull(AllocationContexts.Receipts).Should().BeFalse();
            peerInfo.IsAllocationFull(AllocationContexts.Bodies).Should().BeTrue();
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

            peerInfo.IsAllocationFull(AllocationContexts.Headers).Should().BeTrue();
            peerInfo.IsAllocationFull(AllocationContexts.Bodies).Should().BeFalse();
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
    }
}
