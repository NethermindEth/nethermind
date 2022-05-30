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

using FluentAssertions;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class WorldStateTests
    {
        [Test]
        public void When_taking_a_snapshot_invokes_take_snapshot_on_both_providers()
        {
            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            IStorageProvider storageProvider = Substitute.For<IStorageProvider>();

            WorldState worldState = new(stateProvider, storageProvider);
            worldState.TakeSnapshot();

            stateProvider.Received().TakeSnapshot();
            storageProvider.Received().TakeSnapshot();
        }

        [Test]
        public void When_taking_a_snapshot_return_the_same_value_as_both()
        {
            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            IStorageProvider storageProvider = Substitute.For<IStorageProvider>();

            WorldState worldState = new(stateProvider, storageProvider);
            Snapshot snapshot = worldState.TakeSnapshot();

            snapshot.StateSnapshot.Should().Be(0);
            snapshot.PersistentStorageSnapshot.Should().Be(0);
        }

        [Test]
        public void When_taking_a_snapshot_can_return_non_zero_snapshot_value()
        {
            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            IStorageProvider storageProvider = Substitute.For<IStorageProvider>();

            WorldState worldState = new(stateProvider, storageProvider);

            stateProvider.TakeSnapshot().Returns(1);
            storageProvider.TakeSnapshot().Returns(new Snapshot(1, 2, 3));
            
            Snapshot snapshot = worldState.TakeSnapshot();
            snapshot.StateSnapshot.Should().Be(1);
            snapshot.PersistentStorageSnapshot.Should().Be(2);
            snapshot.TransientStorageSnapshot.Should().Be(3);
        }
        
        [Test]
        public void When_taking_a_snapshot_can_specify_transaction_boundary()
        {
            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            IStorageProvider storageProvider = Substitute.For<IStorageProvider>();

            WorldState worldState = new(stateProvider, storageProvider);
            _ = worldState.TakeSnapshot(true);
            storageProvider.Received().TakeSnapshot(true);
        }
        
        [Test]
        public void Can_restore_snapshot()
        {
            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            IStorageProvider storageProvider = Substitute.For<IStorageProvider>();

            WorldState worldState = new(stateProvider, storageProvider);
            worldState.Restore(new Snapshot(1, 2, 1));
            stateProvider.Received().Restore(1);
            storageProvider.Received().Restore(new Snapshot(1, 2, 1));
        }
    }
}
