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
    public class StorageProviderTests
    {
        private static readonly ILogManager LogManager = LimboLogs.Instance;

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
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            provider.Commit(Frontier.Instance);
            provider.Restore(Snapshot.Empty);
        }

        private WorldState BuildStorageProvider(Context ctx)
        {
            return ctx.StateProvider;
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_same_index_different_values_restore(int snapshot)
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
            provider.Restore(Snapshot.EmptyPosition, snapshot, Snapshot.EmptyPosition);

            Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[snapshot + 1]));
        }

        [Test]
        public void Keep_in_cache()
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Commit(Frontier.Instance);
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Restore(Snapshot.EmptyPosition, -1, Snapshot.EmptyPosition);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Restore(Snapshot.EmptyPosition, -1, Snapshot.EmptyPosition);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Restore(Snapshot.EmptyPosition, -1, Snapshot.EmptyPosition);
            Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[1]));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_different_index(int snapshot)
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
            provider.Restore(Snapshot.EmptyPosition, snapshot, Snapshot.EmptyPosition);

            Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[Math.Min(snapshot + 1, 1)]));
        }

        [Test]
        public void Commit_restore()
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
            provider.Commit(Frontier.Instance);
            provider.Set(new StorageCell(ctx.Address2, 1), _values[4]);
            provider.Set(new StorageCell(ctx.Address2, 2), _values[5]);
            provider.Set(new StorageCell(ctx.Address2, 3), _values[6]);
            provider.Commit(Frontier.Instance);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[7]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[8]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[9]);
            provider.Commit(Frontier.Instance);
            provider.Set(new StorageCell(ctx.Address2, 1), _values[10]);
            provider.Set(new StorageCell(ctx.Address2, 2), _values[11]);
            provider.Set(new StorageCell(ctx.Address2, 3), _values[12]);
            provider.Commit(Frontier.Instance);
            provider.Restore(Snapshot.Empty);

            Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[7]));
            Assert.That(provider.Get(new StorageCell(ctx.Address1, 2)), Is.EqualTo(_values[8]));
            Assert.That(provider.Get(new StorageCell(ctx.Address1, 3)), Is.EqualTo(_values[9]));
            Assert.That(provider.Get(new StorageCell(ctx.Address2, 1)), Is.EqualTo(_values[10]));
            Assert.That(provider.Get(new StorageCell(ctx.Address2, 2)), Is.EqualTo(_values[11]));
            Assert.That(provider.Get(new StorageCell(ctx.Address2, 3)), Is.EqualTo(_values[12]));
        }

        [Test]
        public void Commit_no_changes()
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
            provider.Restore(Snapshot.Empty);
            provider.Commit(Frontier.Instance);

            Assert.IsTrue(provider.Get(new StorageCell(ctx.Address1, 1)).IsZero());
        }

        [Test]
        public void Commit_no_changes_2()
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
            provider.Restore(Snapshot.EmptyPosition, 2, Snapshot.EmptyPosition);
            provider.Restore(Snapshot.EmptyPosition, 1, Snapshot.EmptyPosition);
            provider.Restore(Snapshot.EmptyPosition, 0, Snapshot.EmptyPosition);
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
            provider.Restore(Snapshot.EmptyPosition, -1, Snapshot.EmptyPosition);
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Commit(Frontier.Instance);

            Assert.True(provider.Get(new StorageCell(ctx.Address1, 1)).IsZero());
        }

        [Test]
        public void Commit_trees_clear_caches_get_previous_root()
        {
            Context ctx = new();
            // block 1
            WorldState storageProvider = BuildStorageProvider(ctx);
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            storageProvider.Commit(Frontier.Instance);

            ctx.StateProvider.Commit(Frontier.Instance);
            ctx.StateProvider.CommitTree(0);

            // block 2
            Keccak stateRoot = ctx.StateProvider.StateRoot;
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            storageProvider.Commit(Frontier.Instance);
            ctx.StateProvider.Commit(Frontier.Instance);

            // revert
            ctx.StateProvider.Reset();
            storageProvider.Reset();
            ctx.StateProvider.StateRoot = stateRoot;

            byte[] valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1));

            Assert.That(valueAfter, Is.EqualTo(_values[1]));
        }

        [Test]
        public void Can_commit_when_exactly_at_capacity_regression()
        {
            Context ctx = new();
            // block 1
            WorldState storageProvider = BuildStorageProvider(ctx);
            for (int i = 0; i < Resettable.StartCapacity; i++)
            {
                storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[i % 2]);
            }

            storageProvider.Commit(Frontier.Instance);
            ctx.StateProvider.Commit(Frontier.Instance);

            byte[] valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1));
            Assert.That(valueAfter, Is.EqualTo(_values[(Resettable.StartCapacity + 1) % 2]));
        }

        /// <summary>
        /// Transient storage should be zero if uninitialized
        /// </summary>
        [Test]
        public void Can_tload_uninitialized_locations()
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            // Should be 0 if not set
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).IsZero());

            // Should be 0 if loading from the same contract but different index
            provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).IsZero());

            // Should be 0 if loading from the same index but different contract
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address2, 1)).IsZero());
        }

        /// <summary>
        /// Simple transient storage test
        /// </summary>
        [Test]
        public void Can_tload_after_tstore()
        {
            Context ctx = new Context();
            WorldState provider = BuildStorageProvider(ctx);

            provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
            Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)), Is.EqualTo(_values[1]));
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
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            Snapshot[] snapshots = new Snapshot[4];
            snapshots[0] = provider.TakeSnapshot();
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[1]);
            snapshots[1] = provider.TakeSnapshot();
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[2]);
            snapshots[2] = provider.TakeSnapshot();
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[3]);
            snapshots[3] = provider.TakeSnapshot();

            Assert.That(snapshot, Is.EqualTo(snapshots[snapshot + 1].StorageSnapshot.TransientStorageSnapshot));
            // Persistent storage is unimpacted by transient storage
            Assert.That(snapshots[snapshot + 1].StorageSnapshot.PersistentStorageSnapshot, Is.EqualTo(-1));

            provider.Restore(snapshots[snapshot + 1]);

            Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[snapshot + 1]));
        }

        /// <summary>
        /// Commit will reset transient state
        /// </summary>
        [Test]
        public void Commit_resets_transient_state()
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);

            provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
            Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)), Is.EqualTo(_values[1]));

            provider.Commit(Frontier.Instance);
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).IsZero());
        }

        /// <summary>
        /// Reset will reset transient state
        /// </summary>
        [Test]
        public void Reset_resets_transient_state()
        {
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);

            provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
            Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)), Is.EqualTo(_values[1]));

            provider.Reset();
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).IsZero());
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
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            Snapshot[] snapshots = new Snapshot[4];

            // No updates
            snapshots[0] = provider.TakeSnapshot();

            // Only update transient
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[1]);
            snapshots[1] = provider.TakeSnapshot();

            // Update both
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[9]);
            snapshots[2] = provider.TakeSnapshot();

            // Only update persistent
            provider.Set(new StorageCell(ctx.Address1, 1), _values[8]);
            snapshots[3] = provider.TakeSnapshot();

            provider.Restore(snapshots[snapshot + 1]);

            // Since we didn't update transient on the 3rd snapshot
            if (snapshot == 2)
            {
                snapshot--;
            }
            snapshots[0].StorageSnapshot.Should().BeEquivalentTo(Snapshot.Storage.Empty);
            snapshots[1].StorageSnapshot.Should().BeEquivalentTo(new Snapshot.Storage(Snapshot.EmptyPosition, 0));
            snapshots[2].StorageSnapshot.Should().BeEquivalentTo(new Snapshot.Storage(0, 1));
            snapshots[3].StorageSnapshot.Should().BeEquivalentTo(new Snapshot.Storage(1, 1));

            _values[snapshot + 1].Should().BeEquivalentTo(provider.GetTransientState(new StorageCell(ctx.Address1, 1)));
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
            Context ctx = new();
            WorldState provider = BuildStorageProvider(ctx);
            Snapshot[] snapshots = new Snapshot[4];

            // No updates
            snapshots[0] = (provider).TakeSnapshot();

            // Only update persistent
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            snapshots[1] = (provider).TakeSnapshot();

            // Update both
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[9]);
            snapshots[2] = (provider).TakeSnapshot();

            // Only update transient
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[8]);
            snapshots[3] = (provider).TakeSnapshot();

            provider.Restore(snapshots[snapshot + 1]);

            // Since we didn't update persistent on the 3rd snapshot
            if (snapshot == 2)
            {
                snapshot--;
            }

            snapshots.Should().Equal(
                Snapshot.Empty,
                new Snapshot(Snapshot.EmptyPosition, new Snapshot.Storage(0, Snapshot.EmptyPosition)),
                new Snapshot(Snapshot.EmptyPosition, new Snapshot.Storage(1, 0)),
                new Snapshot(Snapshot.EmptyPosition, new Snapshot.Storage(1, 1))
            );

            _values[snapshot + 1].Should().BeEquivalentTo(provider.Get(new StorageCell(ctx.Address1, 1)));
        }

        private class Context
        {
            public WorldState StateProvider { get; }

            public readonly Address Address1 = new(Keccak.Compute("1"));
            public readonly Address Address2 = new(Keccak.Compute("2"));

            public Context()
            {
                StateProvider = new WorldState(new TrieStore(new MemDb(), LimboLogs.Instance), Substitute.For<IDb>(), LogManager);
                StateProvider.CreateAccount(Address1, 0);
                StateProvider.CreateAccount(Address2, 0);
                StateProvider.Commit(Frontier.Instance);
            }
        }
    }
}
