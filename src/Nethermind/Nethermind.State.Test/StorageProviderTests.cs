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
using Nethermind.Int256;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StorageProviderTests
    {
        private static readonly ILogManager LogManager = LimboLogs.Instance;

        private readonly UInt256[] _values =
        {
            (UInt256)0,
            (UInt256)1,
            (UInt256)2,
            (UInt256)3,
            (UInt256)4,
            (UInt256)5,
            (UInt256)6,
            (UInt256)7,
            (UInt256)8,
            (UInt256)9,
            (UInt256)10,
            (UInt256)11,
            (UInt256)12,
        };

        [Test]
        public void Empty_commit_restore()
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);
            provider.Commit();
            provider.Restore(Snapshot.Storage.Empty);
        }

        private StorageProvider BuildStorageProvider(Context ctx)
        {
            StorageProvider provider = new(new TrieStore(new MemDb(), LogManager), ctx.StateProvider, LogManager);
            return provider;
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_same_index_different_values_restore(int snapshot)
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
            provider.Restore(snapshot);

            Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[snapshot + 1]));
        }

        [Test]
        public void Keep_in_cache()
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Commit();
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Restore(-1);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Restore(-1);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Restore(-1);
            Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[1]));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Same_address_different_index(int snapshot)
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
            provider.Restore(snapshot);

            Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)), Is.EqualTo(_values[Math.Min(snapshot + 1, 1)]));
        }

        [Test]
        public void Commit_restore()
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
            provider.Commit();
            provider.Set(new StorageCell(ctx.Address2, 1), _values[4]);
            provider.Set(new StorageCell(ctx.Address2, 2), _values[5]);
            provider.Set(new StorageCell(ctx.Address2, 3), _values[6]);
            provider.Commit();
            provider.Set(new StorageCell(ctx.Address1, 1), _values[7]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[8]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[9]);
            provider.Commit();
            provider.Set(new StorageCell(ctx.Address2, 1), _values[10]);
            provider.Set(new StorageCell(ctx.Address2, 2), _values[11]);
            provider.Set(new StorageCell(ctx.Address2, 3), _values[12]);
            provider.Commit();
            provider.Restore(-1);

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
            StorageProvider provider = BuildStorageProvider(ctx);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
            provider.Restore(-1);
            provider.Commit();

            Assert.IsTrue(provider.Get(new StorageCell(ctx.Address1, 1)).IsZero);
        }

        [Test]
        public void Commit_no_changes_2()
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
            provider.Restore(2);
            provider.Restore(1);
            provider.Restore(0);
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
            provider.Restore(-1);
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Get(new StorageCell(ctx.Address1, 1));
            provider.Commit();

            Assert.True(provider.Get(new StorageCell(ctx.Address1, 1)).IsZero);
        }

        [Test]
        public void Commit_trees_clear_caches_get_previous_root()
        {
            Context ctx = new();
            // block 1
            StorageProvider storageProvider = BuildStorageProvider(ctx);
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            storageProvider.Commit();

            ctx.StateProvider.Commit(Frontier.Instance);
            storageProvider.CommitTrees(0);
            ctx.StateProvider.CommitTree(0);

            // block 2
            Keccak stateRoot = ctx.StateProvider.StateRoot;
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            storageProvider.Commit();
            ctx.StateProvider.Commit(Frontier.Instance);

            // revert
            ctx.StateProvider.Reset();
            storageProvider.Reset();
            ctx.StateProvider.StateRoot = stateRoot;

            UInt256 valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1));

            Assert.That(valueAfter, Is.EqualTo(_values[1]));
        }

        [Test]
        public void Can_commit_when_exactly_at_capacity_regression()
        {
            Context ctx = new();
            // block 1
            StorageProvider storageProvider = BuildStorageProvider(ctx);
            for (int i = 0; i < Resettable.StartCapacity; i++)
            {
                storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[i % 2]);
            }

            storageProvider.Commit();
            ctx.StateProvider.Commit(Frontier.Instance);

            UInt256 valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1));
            Assert.That(valueAfter, Is.EqualTo(_values[(Resettable.StartCapacity + 1) % 2]));
        }

        /// <summary>
        /// Transient storage should be zero if uninitialized
        /// </summary>
        [Test]
        public void Can_tload_uninitialized_locations()
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);
            // Should be 0 if not set
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).IsZero);

            // Should be 0 if loading from the same contract but different index
            provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).IsZero);

            // Should be 0 if loading from the same index but different contract
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address2, 1)).IsZero);
        }

        /// <summary>
        /// Simple transient storage test
        /// </summary>
        [Test]
        public void Can_tload_after_tstore()
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);

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
            StorageProvider provider = BuildStorageProvider(ctx);
            Snapshot.Storage[] snapshots = new Snapshot.Storage[4];
            snapshots[0] = ((IStorageProvider)provider).TakeSnapshot();
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[1]);
            snapshots[1] = ((IStorageProvider)provider).TakeSnapshot();
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[2]);
            snapshots[2] = ((IStorageProvider)provider).TakeSnapshot();
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[3]);
            snapshots[3] = ((IStorageProvider)provider).TakeSnapshot();

            Assert.That(snapshot, Is.EqualTo(snapshots[snapshot + 1].TransientStorageSnapshot));
            // Persistent storage is unimpacted by transient storage
            Assert.That(snapshots[snapshot + 1].PersistentStorageSnapshot, Is.EqualTo(-1));
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
            StorageProvider provider = BuildStorageProvider(ctx);

            provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
            Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)), Is.EqualTo(_values[1]));

            provider.Commit();
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).IsZero);
        }

        /// <summary>
        /// Reset will reset transient state
        /// </summary>
        [Test]
        public void Reset_resets_transient_state()
        {
            Context ctx = new();
            StorageProvider provider = BuildStorageProvider(ctx);

            provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
            Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)), Is.EqualTo(_values[1]));

            provider.Reset();
            Assert.True(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).IsZero);
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
            StorageProvider provider = BuildStorageProvider(ctx);
            Snapshot.Storage[] snapshots = new Snapshot.Storage[4];

            // No updates
            snapshots[0] = ((IStorageProvider)provider).TakeSnapshot();

            // Only update transient
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[1]);
            snapshots[1] = ((IStorageProvider)provider).TakeSnapshot();

            // Update both
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.Set(new StorageCell(ctx.Address1, 1), _values[9]);
            snapshots[2] = ((IStorageProvider)provider).TakeSnapshot();

            // Only update persistent
            provider.Set(new StorageCell(ctx.Address1, 1), _values[8]);
            snapshots[3] = ((IStorageProvider)provider).TakeSnapshot();

            provider.Restore(snapshots[snapshot + 1]);

            // Since we didn't update transient on the 3rd snapshot
            if (snapshot == 2)
            {
                snapshot--;
            }

            snapshots.Should().Equal(
                Snapshot.Storage.Empty,
                new Snapshot.Storage(Snapshot.EmptyPosition, 0),
                new Snapshot.Storage(0, 1),
                new Snapshot.Storage(1, 1));

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
            StorageProvider provider = BuildStorageProvider(ctx);
            Snapshot.Storage[] snapshots = new Snapshot.Storage[4];

            // No updates
            snapshots[0] = ((IStorageProvider)provider).TakeSnapshot();

            // Only update persistent
            provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            snapshots[1] = ((IStorageProvider)provider).TakeSnapshot();

            // Update both
            provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[9]);
            snapshots[2] = ((IStorageProvider)provider).TakeSnapshot();

            // Only update transient
            provider.SetTransientState(new StorageCell(ctx.Address1, 1), _values[8]);
            snapshots[3] = ((IStorageProvider)provider).TakeSnapshot();

            provider.Restore(snapshots[snapshot + 1]);

            // Since we didn't update persistent on the 3rd snapshot
            if (snapshot == 2)
            {
                snapshot--;
            }

            snapshots.Should().Equal(
                Snapshot.Storage.Empty,
                new Snapshot.Storage(0, Snapshot.EmptyPosition),
                new Snapshot.Storage(1, 0),
                new Snapshot.Storage(1, 1));

            _values[snapshot + 1].Should().BeEquivalentTo(provider.Get(new StorageCell(ctx.Address1, 1)));
        }

        private class Context
        {
            public IStateProvider StateProvider { get; }

            public readonly Address Address1 = new(Keccak.Compute("1"));
            public readonly Address Address2 = new(Keccak.Compute("2"));

            public Context()
            {
                StateProvider = new StateProvider(new TrieStore(new MemDb(), LimboLogs.Instance), Substitute.For<IDb>(), LogManager);
                StateProvider.CreateAccount(Address1, 0);
                StateProvider.CreateAccount(Address2, 0);
                StateProvider.Commit(Frontier.Instance);
            }
        }
    }
}
