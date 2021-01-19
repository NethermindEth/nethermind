//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
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
    [TestFixture(AllocationContexts.Witness)]
    [TestFixture(AllocationContexts.All)]
    [Parallelizable(ParallelScope.All)]
    public class PeerInfoTests
    {
        private readonly AllocationContexts _contexts;

        public PeerInfoTests(AllocationContexts contexts)
        {
            _contexts = contexts;
        }

        [Test]
        public void Can_put_to_sleep_by_contexts()
        {
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
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
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
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
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
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
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
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
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
            peerInfo.IsAllocated(_contexts).Should().BeFalse();
            peerInfo.TryAllocate(_contexts);
            peerInfo.IsAllocated(_contexts).Should().BeTrue();
            peerInfo.CanBeAllocated(_contexts).Should().BeFalse();
        }
        
        [Test]
        public void Can_free()
        {
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
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
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
            peerInfo.TryAllocate(AllocationContexts.Blocks);
            peerInfo.IsAllocated(AllocationContexts.Bodies).Should().BeTrue();
            peerInfo.IsAllocated(AllocationContexts.Headers).Should().BeTrue();
            peerInfo.IsAllocated(AllocationContexts.Receipts).Should().BeTrue();
            peerInfo.CanBeAllocated(AllocationContexts.Bodies).Should().BeFalse();
            peerInfo.CanBeAllocated(AllocationContexts.Headers).Should().BeFalse();
            peerInfo.CanBeAllocated(AllocationContexts.Receipts).Should().BeFalse();
            
            peerInfo.Free(AllocationContexts.Receipts);
            peerInfo.IsAllocated(AllocationContexts.Receipts).Should().BeFalse();
            peerInfo.IsAllocated(AllocationContexts.Bodies).Should().BeTrue();
        }
        
        [Test]
        public void Cannot_allocate_subcontext_of_sleeping()
        {
            PeerInfo peerInfo = new PeerInfo(Substitute.For<ISyncPeer>());
            peerInfo.PutToSleep(AllocationContexts.Blocks, DateTime.MinValue);
            peerInfo.CanBeAllocated(AllocationContexts.Bodies).Should().BeFalse();
        }
    }
}
