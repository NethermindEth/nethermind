// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Db;
using Nethermind.Specs.Forks;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class WorldStorageTests
    {
        private static readonly ILogManager _logManager = LimboLogs.Instance;
        private static readonly Address _address1 = new(Keccak.Compute("1"));
        private static readonly Address _address2 = new(Keccak.Compute("2"));

        private static WorldState GetProvider()
        {
            WorldState provider = new(new TrieStore(new MemDb(), LimboLogs.Instance), Substitute.For<IDb>(), _logManager);
            provider.CreateAccount(_address1, 0);
            provider.CreateAccount(_address2, 0);
            provider.Commit(Frontier.Instance);
            return provider;
        }


        private readonly byte[][] _values =
        {
            new byte[] {0},
            new byte[] {1},
            new byte[] {2},
            new byte[] {3},
            new byte[] {4},
            new byte[] {5},
            new byte[] {6},
            new byte[] {7},
            new byte[] {8},
            new byte[] {9},
            new byte[] {10},
            new byte[] {11},
            new byte[] {12},
        };

        [Test]
        public void Empty_commit_restore()
        {
            WorldState provider = GetProvider();
            provider.Commit();
            provider.RestoreStorage(Snapshot.Storage.Empty);
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_same_index_different_values_restore(int snapshot)
        {
            WorldState provider = GetProvider();
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.Set(new StorageCell(_address1, 1), _values[3]);
            provider.RestoreStorage(snapshot);

            Assert.AreEqual(_values[snapshot + 1], provider.Get(new StorageCell(_address1, 1)));
        }

        [Test]
        public void Keep_in_cache()
        {
            WorldState provider = GetProvider();
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Commit();
            provider.Get(new StorageCell(_address1, 1));
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.RestoreStorage(-1);
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.RestoreStorage(-1);
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.RestoreStorage(-1);
            Assert.AreEqual(_values[1], provider.Get(new StorageCell(_address1, 1)));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_different_index(int snapshot)
        {
            WorldState provider = GetProvider();
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Set(new StorageCell(_address1, 2), _values[2]);
            provider.Set(new StorageCell(_address1, 3), _values[3]);
            provider.RestoreStorage(snapshot);

            Assert.AreEqual(_values[Math.Min(snapshot + 1, 1)], provider.Get(new StorageCell(_address1, 1)));
        }

        [Test]
        public void Commit_restore()
        {
            WorldState provider = GetProvider();
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Set(new StorageCell(_address1, 2), _values[2]);
            provider.Set(new StorageCell(_address1, 3), _values[3]);
            provider.Commit();
            provider.Set(new StorageCell(_address2, 1), _values[4]);
            provider.Set(new StorageCell(_address2, 2), _values[5]);
            provider.Set(new StorageCell(_address2, 3), _values[6]);
            provider.Commit();
            provider.Set(new StorageCell(_address1, 1), _values[7]);
            provider.Set(new StorageCell(_address1, 2), _values[8]);
            provider.Set(new StorageCell(_address1, 3), _values[9]);
            provider.Commit();
            provider.Set(new StorageCell(_address2, 1), _values[10]);
            provider.Set(new StorageCell(_address2, 2), _values[11]);
            provider.Set(new StorageCell(_address2, 3), _values[12]);
            provider.Commit();
            provider.RestoreStorage(-1);

            Assert.AreEqual(_values[7], provider.Get(new StorageCell(_address1, 1)));
            Assert.AreEqual(_values[8], provider.Get(new StorageCell(_address1, 2)));
            Assert.AreEqual(_values[9], provider.Get(new StorageCell(_address1, 3)));
            Assert.AreEqual(_values[10], provider.Get(new StorageCell(_address2, 1)));
            Assert.AreEqual(_values[11], provider.Get(new StorageCell(_address2, 2)));
            Assert.AreEqual(_values[12], provider.Get(new StorageCell(_address2, 3)));
        }

        [Test]
        public void Commit_no_changes()
        {
            WorldState provider = GetProvider();
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Set(new StorageCell(_address1, 2), _values[2]);
            provider.Set(new StorageCell(_address1, 3), _values[3]);
            provider.RestoreStorage(-1);
            provider.Commit();

            Assert.IsTrue(provider.Get(new StorageCell(_address1, 1)).IsZero());
        }

        [Test]
        public void Commit_no_changes_2()
        {
            WorldState provider = GetProvider();
            provider.Get(new StorageCell(_address1, 1));
            provider.Get(new StorageCell(_address1, 1));
            provider.Get(new StorageCell(_address1, 1));
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.Set(new StorageCell(_address1, 1), _values[3]);
            provider.RestoreStorage(2);
            provider.RestoreStorage(1);
            provider.RestoreStorage(0);
            provider.Get(new StorageCell(_address1, 1));
            provider.Get(new StorageCell(_address1, 1));
            provider.Get(new StorageCell(_address1, 1));
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.Set(new StorageCell(_address1, 1), _values[3]);
            provider.RestoreStorage(-1);
            provider.Get(new StorageCell(_address1, 1));
            provider.Get(new StorageCell(_address1, 1));
            provider.Get(new StorageCell(_address1, 1));
            provider.Commit();

            Assert.True(provider.Get(new StorageCell(_address1, 1)).IsZero());
        }

        [Test]
        public void Commit_trees_clear_caches_get_previous_root()
        {
            WorldState provider = GetProvider();
            // block 1
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            // block 2
            Keccak stateRoot = provider.StateRoot;
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.Commit(Frontier.Instance);

            // revert
            provider.Reset();
            provider.StateRoot = stateRoot;

            byte[] valueAfter = provider.Get(new StorageCell(_address1, 1));

            Assert.AreEqual(_values[1], valueAfter);
        }

        [Test]
        public void Can_commit_when_exactly_at_capacity_regression()
        {
            WorldState provider = GetProvider();
            for (int i = 0; i < Resettable.StartCapacity; i++)
            {
                provider.Set(new StorageCell(_address1, 1), _values[i % 2]);
            }

            provider.Commit(Frontier.Instance);

            byte[] valueAfter = provider.Get(new StorageCell(_address1, 1));
            Assert.AreEqual(_values[(Resettable.StartCapacity + 1) % 2], valueAfter);
        }

        /// <summary>
        /// Transient storage should be zero if uninitialized
        /// </summary>
        [Test]
        public void Can_tload_uninitialized_locations()
        {
            WorldState provider = GetProvider();

            // Should be 0 if not set
            Assert.True(provider.GetTransientState(new StorageCell(_address1, 1)).IsZero());

            // Should be 0 if loading from the same contract but different index
            provider.SetTransientState(new StorageCell(_address1, 2), _values[1]);
            Assert.True(provider.GetTransientState(new StorageCell(_address1, 1)).IsZero());

            // Should be 0 if loading from the same index but different contract
            Assert.True(provider.GetTransientState(new StorageCell(_address2, 1)).IsZero());
        }

        /// <summary>
        /// Simple transient storage test
        /// </summary>
        [Test]
        public void Can_tload_after_tstore()
        {
            WorldState provider = GetProvider();
            provider.SetTransientState(new StorageCell(_address1, 2), _values[1]);
            Assert.AreEqual(_values[1], provider.GetTransientState(new StorageCell(_address1, 2)));
        }

        /// <summary>
        /// Transient storage can be updated and restored
        /// </summary>
        /// <param name="snapshot">Snapshot to restore to</param>
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Tload_same_address_same_index_different_values_restore(int snapshot)
        {
            WorldState provider = GetProvider();

            Snapshot[] snapshots = new Snapshot[4];
            Snapshot.Storage[] storageSnapshots = new Snapshot.Storage[4];

            snapshots[0] = provider.TakeSnapshot(false);
            storageSnapshots[0] = snapshots[0].StorageSnapshot;
            provider.SetTransientState(new StorageCell(_address1, 1), _values[1]);
            snapshots[1] = provider.TakeSnapshot(false);
            storageSnapshots[1] = snapshots[1].StorageSnapshot;
            provider.SetTransientState(new StorageCell(_address1, 1), _values[2]);
            snapshots[2] = provider.TakeSnapshot(false);
            storageSnapshots[2] = snapshots[2].StorageSnapshot;
            provider.SetTransientState(new StorageCell(_address1, 1), _values[3]);
            snapshots[3] = provider.TakeSnapshot(false);
            storageSnapshots[3] = snapshots[3].StorageSnapshot;

            Assert.AreEqual(snapshots[snapshot + 1].StorageSnapshot.TransientStorageSnapshot, snapshot);
            // Persistent storage is unimpacted by transient storage
            Assert.AreEqual(snapshots[snapshot + 1].StorageSnapshot.PersistentStorageSnapshot, -1);
            provider.RestoreStorage(snapshots[snapshot + 1].StorageSnapshot);

            Assert.AreEqual(_values[snapshot + 1], provider.GetTransientState(new StorageCell(_address1, 1)));
        }

        /// <summary>
        /// Commit will reset transient state
        /// </summary>
        [Test]
        public void Commit_resets_transient_state()
        {
            WorldState provider = GetProvider();

            provider.SetTransientState(new StorageCell(_address1, 2), _values[1]);
            Assert.AreEqual(_values[1], provider.GetTransientState(new StorageCell(_address1, 2)));

            provider.Commit(Frontier.Instance);
            Assert.True(provider.GetTransientState(new StorageCell(_address1, 2)).IsZero());
        }

        /// <summary>
        /// Reset will reset transient state
        /// </summary>
        [Test]
        public void Reset_resets_transient_state()
        {
            WorldState provider = GetProvider();


            provider.SetTransientState(new StorageCell(_address1, 2), _values[1]);
            Assert.AreEqual(_values[1], provider.GetTransientState(new StorageCell(_address1, 2)));

            provider.Reset();
            Assert.True(provider.GetTransientState(new StorageCell(_address1, 2)).IsZero());
        }

        /// <summary>
        /// Transient state does not impact persistent state
        /// </summary>
        /// <param name="snapshot">Snapshot to restore to</param>
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Transient_state_restores_independent_of_persistent_state(int snapshot)
        {
            WorldState provider = GetProvider();

            Snapshot[] snapshots = new Snapshot[4];
            Snapshot.Storage[] storageSnapshots = new Snapshot.Storage[4];
            // No updates
            snapshots[0] = provider.TakeSnapshot(false);
            storageSnapshots[0] = snapshots[0].StorageSnapshot;

            // Only update transient
            provider.SetTransientState(new StorageCell(_address1, 1), _values[1]);
            snapshots[1] = provider.TakeSnapshot(false);
            storageSnapshots[1] = snapshots[1].StorageSnapshot;

            // Update both
            provider.SetTransientState(new StorageCell(_address1, 1), _values[2]);
            provider.Set(new StorageCell(_address1, 1), _values[9]);
            snapshots[2] = provider.TakeSnapshot(false);
            storageSnapshots[2] = snapshots[2].StorageSnapshot;

            // Only update persistent
            provider.Set(new StorageCell(_address1, 1), _values[8]);
            snapshots[3] = provider.TakeSnapshot(false);
            storageSnapshots[3] = snapshots[3].StorageSnapshot;

            provider.RestoreStorage(snapshots[snapshot + 1].StorageSnapshot);

            // Since we didn't update transient on the 3rd snapshot
            if (snapshot == 2)
            {
                snapshot--;
            }

            storageSnapshots.Should().Equal(
                Snapshot.Storage.Empty,
                new Snapshot.Storage(Snapshot.EmptyPosition, 0),
                new Snapshot.Storage(0, 1),
                new Snapshot.Storage(1, 1));

            _values[snapshot + 1].Should().BeEquivalentTo(provider.GetTransientState(new StorageCell(_address1, 1)));
        }

        /// <summary>
        /// Persistent state does not impact transient state
        /// </summary>
        /// <param name="snapshot">Snapshot to restore to</param>
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Persistent_state_restores_independent_of_transient_state(int snapshot)
        {
            WorldState provider = GetProvider();

            Snapshot[] snapshots = new Snapshot[4];
            Snapshot.Storage[] storageSnapshots = new Snapshot.Storage[4];


            // No updates
            snapshots[0] = provider.TakeSnapshot(false);
            storageSnapshots[0] = snapshots[0].StorageSnapshot;

            // Only update persistent
            provider.Set(new StorageCell(_address1, 1), _values[1]);
            snapshots[1] = (provider).TakeSnapshot(false);
            storageSnapshots[1] = snapshots[1].StorageSnapshot;

            // Update both
            provider.Set(new StorageCell(_address1, 1), _values[2]);
            provider.SetTransientState(new StorageCell(_address1, 1), _values[9]);
            snapshots[2] = (provider).TakeSnapshot(false);
            storageSnapshots[2] = snapshots[2].StorageSnapshot;

            // Only update transient
            provider.SetTransientState(new StorageCell(_address1, 1), _values[8]);
            snapshots[3] = (provider).TakeSnapshot(false);
            storageSnapshots[3] = snapshots[3].StorageSnapshot;

            provider.Restore(snapshots[snapshot + 1]);

            // Since we didn't update persistent on the 3rd snapshot
            if (snapshot == 2)
            {
                snapshot--;
            }

            storageSnapshots.Should().Equal(
                Snapshot.Storage.Empty,
                new Snapshot.Storage(0, Snapshot.EmptyPosition),
                new Snapshot.Storage(1, 0),
                new Snapshot.Storage(1, 1));

            _values[snapshot + 1].Should().BeEquivalentTo(provider.Get(new StorageCell(_address1, 1)));
        }
    }
}
