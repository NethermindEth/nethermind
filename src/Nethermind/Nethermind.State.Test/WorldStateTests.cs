// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            snapshot.StorageSnapshot.PersistentStorageSnapshot.Should().Be(0);
        }

        [Test]
        public void When_taking_a_snapshot_can_return_non_zero_snapshot_value()
        {
            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            IStorageProvider storageProvider = Substitute.For<IStorageProvider>();

            WorldState worldState = new(stateProvider, storageProvider);

            stateProvider.TakeSnapshot().Returns(1);
            storageProvider.TakeSnapshot().Returns(new Snapshot.Storage(2, 3));

            Snapshot snapshot = worldState.TakeSnapshot();
            snapshot.StateSnapshot.Should().Be(1);
            snapshot.StorageSnapshot.PersistentStorageSnapshot.Should().Be(2);
            snapshot.StorageSnapshot.TransientStorageSnapshot.Should().Be(3);
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
            worldState.Restore(new Snapshot(1, new Snapshot.Storage(2, 1)));
            stateProvider.Received().Restore(1);
            storageProvider.Received().Restore(new Snapshot.Storage(2, 1));
        }
    }
}
