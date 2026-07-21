// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Specs.Forks;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Evm.Tracing.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class StorageProviderTests(bool useFlat)
{
    private static readonly ILogManager LogManager = LimboLogs.Instance;

    private readonly byte[][] _values =
    [
        [0],
        [1],
        [2],
        [3],
        [4],
        [5],
        [6],
        [7],
        [8],
        [9],
        [10],
        [11],
        [12],
    ];

    [Test]
    public void Empty_commit_restore()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Commit(Frontier.Instance);
        provider.Restore(Snapshot.Empty);
    }

    private WorldState BuildStorageProvider(Context ctx) => ctx.StateProvider;

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Same_address_same_index_different_values_restore(int snapshot)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
        provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
        provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
        provider.Restore(Snapshot.EmptyPosition, snapshot, Snapshot.EmptyPosition);

        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[snapshot + 1]));
    }

    [Test]
    public void Keep_in_cache()
    {
        using Context ctx = new(useFlat);
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
        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[1]));
    }

    [Test]
    public void Original_value_tracks_transaction_start_across_stacked_writes()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        // tx0: capture the block original (zero), then write; changes stay uncommitted (BuildUp stacking).
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell);
        provider.Set(cell, _values[1]);

        // tx1 stacks on tx0. Its original is the value entering tx1 (_values[1]) and must stay stable
        // across repeated same-slot writes (the case the removed chain walk resolved in O(N^2)).
        provider.TakeSnapshot(newTransactionStart: true);
        Assert.That(provider.GetOriginal(cell).ToArray(), Is.EqualTo(_values[1]));
        for (int i = 2; i <= 6; i++)
        {
            provider.Set(cell, _values[i]);
            Assert.That(provider.GetOriginal(cell).ToArray(), Is.EqualTo(_values[1]));
        }

        // A revert within tx1 must leave the transaction original unchanged.
        int mid = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(cell, _values[7]);
        provider.Restore(Snapshot.EmptyPosition, mid, Snapshot.EmptyPosition);
        Assert.That(provider.GetOriginal(cell).ToArray(), Is.EqualTo(_values[1]));
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Same_address_different_index(int snapshot)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
        provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
        provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
        provider.Restore(Snapshot.EmptyPosition, snapshot, Snapshot.EmptyPosition);

        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[Math.Min(snapshot + 1, 1)]));
    }

    [Test]
    public void Commit_restore()
    {
        using Context ctx = new(useFlat);
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

        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[7]));
        Assert.That(provider.Get(new StorageCell(ctx.Address1, 2)).ToArray(), Is.EqualTo(_values[8]));
        Assert.That(provider.Get(new StorageCell(ctx.Address1, 3)).ToArray(), Is.EqualTo(_values[9]));
        Assert.That(provider.Get(new StorageCell(ctx.Address2, 1)).ToArray(), Is.EqualTo(_values[10]));
        Assert.That(provider.Get(new StorageCell(ctx.Address2, 2)).ToArray(), Is.EqualTo(_values[11]));
        Assert.That(provider.Get(new StorageCell(ctx.Address2, 3)).ToArray(), Is.EqualTo(_values[12]));
    }

    [Test]
    public void Commit_no_changes()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
        provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
        provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
        provider.Restore(Snapshot.Empty);
        provider.Commit(Frontier.Instance);

        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).IsZero(), Is.True);
    }

    [Test]
    public void Commit_no_changes_2()
    {
        using Context ctx = new(useFlat);
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

        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).IsZero(), Is.True);
    }

    [Test]
    public void Commit_trees_clear_caches_get_previous_root()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        // block 1
        Hash256 stateRoot;
        WorldState storageProvider = BuildStorageProvider(ctx);
        using (IDisposable _ = storageProvider.BeginScope(IWorldState.PreGenesis))
        {
            storageProvider.CreateAccount(ctx.Address1, 0);
            storageProvider.CreateAccount(ctx.Address2, 0);
            storageProvider.Commit(Frontier.Instance);
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
            storageProvider.Commit(Frontier.Instance);
            storageProvider.Commit(Frontier.Instance);
            storageProvider.CommitTree(0);
            stateRoot = ctx.StateProvider.StateRoot;
        }
        BlockHeader newBase = Build.A.BlockHeader.WithStateRoot(stateRoot).TestObject;

        // block 2
        using (IDisposable _ = storageProvider.BeginScope(newBase))
        {
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            storageProvider.Commit(Frontier.Instance);
            storageProvider.CommitTree(0);
        }

        using (IDisposable _ = storageProvider.BeginScope(newBase))
        {
            Assert.That(storageProvider.AccountExists(ctx.Address1), Is.True);

            byte[] valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1)).ToArray();

            Assert.That(valueAfter, Is.EqualTo(_values[1]));
        }
    }

    [Test]
    public void Storage_root_collect_recomputes_all_changed_contracts_amid_warm_reads()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);

        Address[] written =
        [
            new(Keccak.Compute("w1")),
            new(Keccak.Compute("w2")),
            new(Keccak.Compute("w3")),
            new(Keccak.Compute("w4")),
        ];

        Hash256 stateRoot;
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            foreach (Address address in written)
            {
                provider.CreateAccount(address, 1);
            }
            provider.Commit(Frontier.Instance);

            for (int i = 0; i < written.Length; i++)
            {
                provider.Set(new StorageCell(written[i], 1), _values[i + 1]);
            }

            for (int i = 0; i < 64; i++)
            {
                provider.Get(new StorageCell(new Address(Keccak.Compute($"r{i}")), 1));
            }

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            stateRoot = provider.StateRoot;
        }

        BlockHeader head = Build.A.BlockHeader.WithStateRoot(stateRoot).TestObject;
        using (provider.BeginScope(head))
        {
            for (int i = 0; i < written.Length; i++)
            {
                Assert.That(provider.Get(new StorageCell(written[i], 1)).ToArray(), Is.EqualTo(_values[i + 1]),
                    $"storage for written contract {i} was not persisted");
            }
        }
    }

    [Test]
    public void Can_commit_when_exactly_at_capacity_regression()
    {
        using Context ctx = new(useFlat);
        // block 1
        WorldState storageProvider = BuildStorageProvider(ctx);
        for (int i = 0; i < Resettable.StartCapacity; i++)
        {
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[i % 2]);
        }

        storageProvider.Commit(Frontier.Instance);
        ctx.StateProvider.Commit(Frontier.Instance);

        byte[] valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1)).ToArray();
        Assert.That(valueAfter, Is.EqualTo(_values[(Resettable.StartCapacity + 1) % 2]));
    }

    /// <summary>
    /// Transient storage should be zero if uninitialized
    /// </summary>
    [Test]
    public void Can_tload_uninitialized_locations()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        // Should be 0 if not set
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).IsZero(), Is.True);

        // Should be 0 if loading from the same contract but different index
        provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).IsZero(), Is.True);

        // Should be 0 if loading from the same index but different contract
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address2, 1)).IsZero(), Is.True);
    }

    /// <summary>
    /// Simple transient storage test
    /// </summary>
    [Test]
    public void Can_tload_after_tstore()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);

        provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).ToArray(), Is.EqualTo(_values[1]));
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
        using Context ctx = new(useFlat);
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

        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[snapshot + 1]));
    }

    /// <summary>
    /// Commit will reset transient state
    /// </summary>
    [Test]
    public void Commit_resets_transient_state()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);

        provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).ToArray(), Is.EqualTo(_values[1]));

        provider.Commit(Frontier.Instance);
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).IsZero(), Is.True);
    }

    /// <summary>
    /// Reset will reset transient state
    /// </summary>
    [Test]
    public void Reset_resets_transient_state()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);

        provider.SetTransientState(new StorageCell(ctx.Address1, 2), _values[1]);
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).ToArray(), Is.EqualTo(_values[1]));

        provider.Reset();
        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 2)).IsZero(), Is.True);
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
        using Context ctx = new(useFlat);
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
        Assert.That(snapshots[0].StorageSnapshot, Is.EqualTo(Snapshot.Storage.Empty));
        Assert.That(snapshots[1].StorageSnapshot, Is.EqualTo(new Snapshot.Storage(Snapshot.EmptyPosition, 0)));
        Assert.That(snapshots[2].StorageSnapshot, Is.EqualTo(new Snapshot.Storage(0, 1)));
        Assert.That(snapshots[3].StorageSnapshot, Is.EqualTo(new Snapshot.Storage(1, 1)));

        Assert.That(_values[snapshot + 1], Is.EqualTo(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).ToArray()));
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
        using Context ctx = new(useFlat);
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

        Assert.That(snapshots, Is.EqualTo(new[] { Snapshot.Empty, new Snapshot(new Snapshot.Storage(0, Snapshot.EmptyPosition), Snapshot.EmptyPosition), new Snapshot(new Snapshot.Storage(1, 0), Snapshot.EmptyPosition), new Snapshot(new Snapshot.Storage(1, 1), Snapshot.EmptyPosition) }));

        Assert.That(_values[snapshot + 1], Is.EqualTo(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray()));
    }

    /// <summary>
    /// Reset will reset transient state
    /// </summary>
    [Test]
    public void Selfdestruct_clears_cache()
    {
        PreBlockCaches preBlockCaches = new();
        using Context ctx = new(useFlat, preBlockCaches: preBlockCaches);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell accessedStorageCell = new(TestItem.AddressA, 1);
        StorageCell nonAccessedStorageCell = new(TestItem.AddressA, 2);
        preBlockCaches.StorageCache.Set(accessedStorageCell, [1, 2, 3]);
        provider.Get(accessedStorageCell);
        provider.Commit(Paris.Instance);
        provider.ClearStorage(TestItem.AddressA);
        Assert.That(provider.Get(accessedStorageCell).ToArray(), Is.EqualTo(StorageTree.ZeroBytes));
        Assert.That(provider.Get(nonAccessedStorageCell).ToArray(), Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void Destroy_only_round_does_not_leak_into_next_transaction()
    {
        // tx1 destroys a contract without touching any storage cell; tx2 (same block)
        // revives the address and writes — a leaked mark would drop tx2's write at commit.
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.MarkStorageDestroyed(ctx.Address1);
        provider.Commit(Frontier.Instance);

        provider.Set(cell, _values[7]);
        provider.Commit(Frontier.Instance);

        Assert.That(provider.Get(cell).ToArray(), Is.EqualTo(_values[7]), "revived contract's write must survive the previous round's destroy mark");
    }

    [Test]
    public void Destroy_of_committed_storage_reads_zero()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(TestItem.AddressA, 1);

        BlockHeader baseBlock = null;
        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.Set(cell, [7]);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            Assert.That(provider.Get(cell).ToArray(), Is.EqualTo(new byte[] { 7 }), "precondition: committed value visible");

            provider.MarkStorageDestroyed(TestItem.AddressA);
            provider.Commit(Frontier.Instance);

            Assert.That(provider.Get(cell).ToArray(), Is.EqualTo(StorageTree.ZeroBytes), "committed prior-block storage must read zero after destroy");
        }
    }

    [Test]
    public void Same_block_revival_reads_zero_for_unrewritten_slots()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell rewritten = new(ctx.Address1, 1);
        StorageCell untouched = new(ctx.Address1, 2);

        provider.Set(rewritten, _values[1]);
        provider.Set(untouched, _values[2]);
        provider.MarkStorageDestroyed(ctx.Address1);
        provider.Commit(Frontier.Instance);

        provider.Set(rewritten, _values[3]);
        provider.Commit(Frontier.Instance);

        Assert.That(provider.Get(rewritten).ToArray(), Is.EqualTo(_values[3]), "revived contract's rewritten slot must hold the new value");
        Assert.That(provider.Get(untouched).ToArray(), Is.EqualTo(StorageTree.ZeroBytes), "un-rewritten slot of a destroyed contract must read zero, not the pre-destroy write");
    }

    [Test]
    public void Destroyed_storage_propagates_to_database_across_blocks()
    {
        // Pre-6780 shape: contract with committed prior-block storage is destroyed via the
        // mark path; a later block must read zero FROM THE DATABASE (the in-block marker is gone).
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(TestItem.AddressA, 1);

        BlockHeader baseBlock = null;
        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.Set(cell, [7]);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            provider.MarkStorageDestroyed(TestItem.AddressA);
            provider.DeleteAccount(TestItem.AddressA);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(baseBlock.Number + 1);
            baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(provider.StateRoot).TestObject;
        }

        // Advance past the flat snapshot retention so the destroy-block diff is pruned
        // from memory and the final read can only be served by the persisted store.
        for (int i = 0; i < 4; i++)
        {
            using (provider.BeginScope(baseBlock))
            {
                provider.Commit(Frontier.Instance);
                provider.CommitTree(baseBlock.Number + 1);
                baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(provider.StateRoot).TestObject;
            }
        }

        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            Assert.That(provider.Get(cell).ToArray(), Is.EqualTo(StorageTree.ZeroBytes), "destroyed storage must be gone from the persisted store, not only from the in-block marker");
        }
    }

    [Test]
    public void Buildup_round_destroy_keeps_later_redeploy_writes()
    {
        // Block production spans the whole block in one round (no per-tx Commit), so the
        // journaled clear must be used there: a redeploy after the destroy writes on top of
        // the zeroing and must survive, while un-rewritten slots stay zero.
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell rewritten = new(ctx.Address1, 1);
        StorageCell untouched = new(ctx.Address1, 2);

        provider.Set(rewritten, _values[1]);
        provider.Set(untouched, _values[2]);
        provider.ClearStorage(ctx.Address1);
        provider.Set(rewritten, _values[3]);
        provider.Commit(Frontier.Instance);

        Assert.That(provider.Get(rewritten).ToArray(), Is.EqualTo(_values[3]), "redeploy write after in-round destroy must survive the commit");
        Assert.That(provider.Get(untouched).ToArray(), Is.EqualTo(StorageTree.ZeroBytes), "un-rewritten slot of the destroyed contract must stay zero");
    }

    [Test]
    public void Selfdestruct_works_across_blocks()
    {
        using Context ctx = new(useFlat, setInitialState: false, trackWrittenData: true);
        WorldState provider = BuildStorageProvider(ctx);

        BlockHeader baseBlock = null;
        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.Set(new StorageCell(TestItem.AddressA, 100), [1]);
            provider.Set(new StorageCell(TestItem.AddressA, 200), [2]);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        Hash256 originalStateRoot = baseBlock.StateRoot;

        ctx.WrittenData.Clear();

        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.ClearStorage(TestItem.AddressA);
            provider.Set(new StorageCell(TestItem.AddressA, 101), [10]);
            provider.Set(new StorageCell(TestItem.AddressA, 200), [2]);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(baseBlock.Number + 1);

            baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(provider.StateRoot).TestObject;
        }

        Assert.That(baseBlock.StateRoot, Is.Not.EqualTo(originalStateRoot));

        Assert.That(ctx.WrittenData.SelfDestructed[TestItem.AddressA], Is.True);
        ctx.WrittenData.Clear();

        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.ClearStorage(TestItem.AddressA);
            provider.Set(new StorageCell(TestItem.AddressA, 100), [1]);
            provider.Set(new StorageCell(TestItem.AddressA, 200), [2]);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(baseBlock.Number + 1);

            baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(provider.StateRoot).TestObject;
        }

        Assert.That(baseBlock.StateRoot, Is.EqualTo(originalStateRoot));

        Assert.That(ctx.WrittenData.SelfDestructed[TestItem.AddressA], Is.True);
    }

    [Test]
    public void Selfdestruct_works_even_when_its_the_only_call()
    {
        using Context ctx = new(useFlat, setInitialState: false, trackWrittenData: true);
        WorldState provider = BuildStorageProvider(ctx);

        BlockHeader baseBlock = null;
        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.Set(new StorageCell(TestItem.AddressA, 100), [1]);
            provider.Set(new StorageCell(TestItem.AddressA, 200), [2]);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        ctx.WrittenData.Clear();

        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.ClearStorage(TestItem.AddressA);
            provider.DeleteAccount(TestItem.AddressA);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(provider.StateRoot).TestObject;
        }

        Assert.That(ctx.WrittenData.SelfDestructed[TestItem.AddressA], Is.True);
        ctx.WrittenData.Clear();

        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            Assert.That(provider.Get(new StorageCell(TestItem.AddressA, 100)).ToArray(), Is.EqualTo(StorageTree.ZeroBytes));

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
        }
    }

    [Test]
    public void Selfdestruct_in_the_same_transaction()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);

        BlockHeader baseBlock = null;
        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.Set(new StorageCell(TestItem.AddressA, 100), [1]);
            provider.Set(new StorageCell(TestItem.AddressA, 200), [2]);
            provider.ClearStorage(TestItem.AddressA);
            provider.DeleteAccount(TestItem.AddressA);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        Assert.That(baseBlock.StateRoot, Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void Selfdestruct_before_commit_will_mark_contract_as_empty()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        IWorldState provider = BuildStorageProvider(ctx);

        BlockHeader baseBlock = null;
        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.Set(new StorageCell(TestItem.AddressA, 100), [1]);
            provider.Set(new StorageCell(TestItem.AddressA, 200), [2]);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            provider.ClearStorage(TestItem.AddressA);
            provider.DeleteAccount(TestItem.AddressA);
            Assert.That(provider.IsStorageEmpty(TestItem.AddressA), Is.True);
        }
    }

    [Test]
    public void Selfdestruct_persist_between_commit()
    {
        PreBlockCaches preBlockCaches = new();
        using Context ctx = new(useFlat, preBlockCaches: preBlockCaches);
        StorageCell accessedStorageCell = new(TestItem.AddressA, 1);
        preBlockCaches.StorageCache.Set(accessedStorageCell, [1, 2, 3]);

        WorldState provider = BuildStorageProvider(ctx);
        Assert.That(provider.Get(accessedStorageCell).ToArray(), Is.EqualTo([1, 2, 3]));
        provider.ClearStorage(TestItem.AddressA);
        provider.Commit(Paris.Instance);
        Assert.That(provider.Get(accessedStorageCell).ToArray(), Is.EqualTo(StorageTree.ZeroBytes));
    }

    [Test]
    public void Commit_ReadOnlyRound_ReportsStorageReadsToTracer()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell readCell = new(TestItem.AddressA, 1);

        provider.Get(readCell);

        ReadCollectingStorageTracer tracer = new();
        provider.Commit(Frontier.Instance, tracer);

        Assert.That(tracer.Reads, Does.Contain(readCell));

        // The round's read capture must be cleared by the read-only commit:
        // a subsequent commit without new reads reports nothing.
        ReadCollectingStorageTracer secondRoundTracer = new();
        provider.Commit(Frontier.Instance, secondRoundTracer);

        Assert.That(secondRoundTracer.Reads, Is.Empty);
    }

    [Test]
    public void Eip161_empty_account_with_storage_does_not_throw_on_commit()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        IWorldState worldState = ctx.StateProvider;
        using IDisposable disposable = worldState.BeginScope(IWorldState.PreGenesis);

        // Create an empty account (balance=0, nonce=0, no code) and set storage on it.
        // EIP-161 (via SpuriousDragon+) deletes empty accounts during commit, but the
        // storage flush has already produced a non-empty storage root. The commit must
        // handle this gracefully by skipping the storage root update for deleted accounts.
        worldState.CreateAccount(TestItem.AddressA, 0);
        worldState.Set(new StorageCell(TestItem.AddressA, 1), [1, 2, 3]);
        worldState.Commit(SpuriousDragon.Instance);

        Assert.That(worldState.AccountExists(TestItem.AddressA), Is.False);
    }

    [Test]
    public void StorageClearSelfDestruct()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        IWorldState worldState = ctx.StateProvider;

        Hash256 stateRoot = null;

        using (IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis))
        {
            // Set something
            worldState.CreateAccount(TestItem.AddressA, 10);
            worldState.Set(new StorageCell(TestItem.AddressA, 1), Bytes.FromHexString("aaaa"));
            worldState.Commit(SpuriousDragon.Instance);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        using (IDisposable _ = worldState.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject))
        {
            // Set storage to a different value
            worldState.Set(new StorageCell(TestItem.AddressA, 1), Bytes.FromHexString("bbbb"));
            worldState.Commit(SpuriousDragon.Instance);

            // Delete but no clear storage
            worldState.DeleteAccount(TestItem.AddressA);
            worldState.Commit(SpuriousDragon.Instance);

            worldState.CommitTree(1);
            stateRoot = worldState.StateRoot;
        }

        using (IDisposable _ = worldState.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            Assert.That(worldState.Get(new StorageCell(TestItem.AddressA, 1)).IsZero(), Is.True);
        }
    }

    [TestCase(2)]
    [TestCase(1000)]
    public void Set_empty_value_for_storage_cell_without_read_clears_data(int numItems)
    {
        using Context ctx = new(useFlat, setInitialState: false);
        IWorldState worldState = ctx.StateProvider;
        using IDisposable disposable = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(TestItem.AddressA, 1);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(0);
        Hash256 emptyHash = worldState.StateRoot;

        for (int i = 0; i < numItems; i++)
        {
            UInt256 asUInt256 = (UInt256)(i + 1);
            worldState.Set(new StorageCell(TestItem.AddressA, (UInt256)i), asUInt256.ToBigEndian());
        }
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(1);

        Hash256 fullHash = worldState.StateRoot;
        Assert.That(fullHash, Is.Not.EqualTo(emptyHash));

        for (int i = 0; i < numItems; i++)
        {
            worldState.Set(new StorageCell(TestItem.AddressA, (UInt256)i), [0]);
        }
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(2);

        Hash256 clearedHash = worldState.StateRoot;

        Assert.That(clearedHash, Is.EqualTo(emptyHash));
    }

    [Test]
    public void Set_empty_value_for_storage_cell_with_read_clears_data()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        IWorldState worldState = ctx.StateProvider;
        using IDisposable disposable = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(TestItem.AddressA, 1);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(0);
        Hash256 emptyHash = worldState.StateRoot;

        worldState.Set(new StorageCell(TestItem.AddressA, 1), _values[11]);
        worldState.Set(new StorageCell(TestItem.AddressA, 2), _values[12]);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(1);

        Hash256 fullHash = worldState.StateRoot;
        Assert.That(fullHash, Is.Not.EqualTo(emptyHash));

        worldState.Get(new StorageCell(TestItem.AddressA, 1));
        worldState.Get(new StorageCell(TestItem.AddressA, 2));
        worldState.Set(new StorageCell(TestItem.AddressA, 1), [0]);
        worldState.Set(new StorageCell(TestItem.AddressA, 2), [0]);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(2);

        Hash256 clearedHash = worldState.StateRoot;

        Assert.That(clearedHash, Is.EqualTo(emptyHash));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Set_pushes_slot_trie_warm_hint_only_from_populator(bool populator)
    {
        PreBlockCaches caches = new();
        IWorldStateScopeProvider.IScope mainScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        caches.MainScope = mainScope;

        using Context ctx = new(useFlat, preBlockCaches: populator ? caches : null);
        caches.MainScope = null;
        ctx.StateProvider.Set(new StorageCell(ctx.Address1, 42), _values[1]);

        if (populator)
            mainScope.Received(1).HintWarmSlot(new ValueAddress(ctx.Address1.Bytes), (UInt256)42);
        else
            mainScope.DidNotReceiveWithAnyArgs().HintWarmSlot(default, default);
    }

    private class Context : IDisposable
    {
        public WorldState StateProvider { get; }
        internal WrittenData WrittenData = null;
        private readonly IContainer _container;

        public readonly Address Address1 = new(Keccak.Compute("1"));
        public readonly Address Address2 = new(Keccak.Compute("2"));

        public Context(bool useFlat, PreBlockCaches preBlockCaches = null, bool setInitialState = true, bool trackWrittenData = false)
        {
            IWorldStateScopeProvider scopeProvider;
            if (useFlat)
            {
                (scopeProvider, _container) = TestWorldStateFactory.CreateFlatScopeProvider();
            }
            else
            {
                scopeProvider = new TrieStoreScopeProvider(
                    TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
                    new MemDb(), LimboLogs.Instance);
            }

            if (preBlockCaches is not null)
            {
                scopeProvider = new PrewarmerScopeProvider(scopeProvider, new PrewarmerState(preBlockCaches, isPrewarmer: true), LimboLogs.Instance);
            }

            if (trackWrittenData)
            {
                WrittenData = new WrittenData(
                    new ConcurrentDictionary<Address, Account>(),
                    new ConcurrentDictionary<StorageCell, byte[]>(),
                    new ConcurrentDictionary<Address, bool>()
                );
                scopeProvider = new WritesInterceptor(scopeProvider, WrittenData);
            }

            StateProvider = new WorldState(scopeProvider, LogManager);
            if (setInitialState)
            {
                StateProvider.BeginScope(IWorldState.PreGenesis);
                StateProvider.CreateAccount(Address1, 0);
                StateProvider.CreateAccount(Address2, 0);
                StateProvider.Commit(Frontier.Instance);
            }
        }

        public void Dispose() => _container?.Dispose();
    }

    internal record WrittenData(
        ConcurrentDictionary<Address, Account> Accounts,
        ConcurrentDictionary<StorageCell, byte[]> Slots,
        ConcurrentDictionary<Address, bool> SelfDestructed)
    {
        public void Clear()
        {
            Accounts.Clear();
            Slots.Clear();
            SelfDestructed.Clear();
        }
    }

    private class WritesInterceptor(IWorldStateScopeProvider scopeProvider, WrittenData writtenData) : IWorldStateScopeProvider
    {

        public bool HasRoot(BlockHeader baseBlock) => scopeProvider.HasRoot(baseBlock);

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader baseBlock, LocalMetrics metrics) => new ScopeDecorator(scopeProvider.BeginScope(baseBlock, metrics), writtenData);

        private class ScopeDecorator(IWorldStateScopeProvider.IScope baseScope, WrittenData writtenData) : IWorldStateScopeProvider.IScope
        {
            public void Dispose() => baseScope.Dispose();

            public Hash256 RootHash => baseScope.RootHash;

            public void UpdateRootHash() => baseScope.UpdateRootHash();

            public Account Get(Address address) => baseScope.Get(address);

            public void HintGet(Address address, Account account) => baseScope.HintGet(address, account);

            public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink sink = null)
                => baseScope.HintBal(bal, sink);

            public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

            public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => baseScope.CreateStorageTree(address);

            public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatchDecorator(baseScope.StartWriteBatch(estimatedAccountNum), writtenData);

            public void Commit(ulong blockNumber) => baseScope.Commit(blockNumber);
        }

        private class WriteBatchDecorator(
            IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch,
            WrittenData writtenData
        )
            : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public void Dispose() => writeBatch.Dispose();

            public event EventHandler<IWorldStateScopeProvider.AccountUpdated> OnAccountUpdated
            {
                add => writeBatch.OnAccountUpdated += value;
                remove => writeBatch.OnAccountUpdated -= value;
            }

            public void Set(Address key, Account account) => writeBatch.Set(key, account);

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) => new StorageWriteBatchDecorator(writeBatch.CreateStorageWriteBatch(key, estimatedEntries), key, writtenData);
        }

        private class StorageWriteBatchDecorator(
            IWorldStateScopeProvider.IStorageWriteBatch baseStorageBatch,
            Address address,
            WrittenData writtenData
        ) : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Dispose() => baseStorageBatch?.Dispose();

            public void Set(in UInt256 index, byte[] value)
            {
                baseStorageBatch.Set(in index, value);
                writtenData.Slots[new StorageCell(address, index)] = value;
            }

            public void Clear()
            {
                baseStorageBatch.Clear();
                writtenData.SelfDestructed[address] = true;
            }
        }
    }

    private sealed class ReadCollectingStorageTracer : IWorldStateTracer
    {
        public System.Collections.Generic.List<StorageCell> Reads { get; } = [];

        public bool IsTracingState => false;
        public bool IsTracingStorage => true;

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) { }
        public void ReportCodeChange(Address address, byte[] before, byte[] after) { }
        public void ReportNonceChange(Address address, UInt256? before, UInt256? after) { }
        public void ReportAccountRead(Address address) { }
        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) { }
        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) { }
        public void ReportStorageRead(in StorageCell storageCell) => Reads.Add(storageCell);
    }
}
