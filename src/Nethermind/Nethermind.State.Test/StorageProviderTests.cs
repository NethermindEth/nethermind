// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Specs.Forks;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[Parallelizable(ParallelScope.All)]
public class StorageProviderTests
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
        int[] snapshots = new int[4];
        snapshots[0] = Snapshot.EmptyPosition;
        provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
        snapshots[1] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
        snapshots[2] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
        snapshots[3] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Restore(Snapshot.EmptyPosition, snapshots[snapshot + 1], Snapshot.EmptyPosition);

        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[snapshot + 1]));
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
        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[1]));
    }

    [Test]
    public void Persistent_restore_same_token_after_noop_restore_undoes_new_writes()
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.Set(cell, _values[1]);
        Snapshot snapshot = provider.TakeSnapshot();

        // No writes after snapshot: this restore trims empty frames only.
        provider.Restore(snapshot);

        provider.Set(cell, _values[2]);
        provider.Restore(snapshot);

        provider.Get(cell).ToArray().Should().BeEquivalentTo(_values[1]);
    }

    [Test]
    public void Persistent_restore_older_token_after_intermediate_noop_restore()
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.Set(cell, _values[1]);
        Snapshot snapshot1 = provider.TakeSnapshot();
        provider.Set(cell, _values[2]);
        Snapshot snapshot2 = provider.TakeSnapshot();

        provider.Restore(snapshot2); // no-op restore (no writes after snapshot2)
        provider.Set(cell, _values[3]);
        provider.Restore(snapshot1);

        provider.Get(cell).ToArray().Should().BeEquivalentTo(_values[1]);
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Same_address_different_index(int snapshot)
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        int[] snapshots = new int[4];
        snapshots[0] = Snapshot.EmptyPosition;
        provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
        snapshots[1] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(new StorageCell(ctx.Address1, 2), _values[2]);
        snapshots[2] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(new StorageCell(ctx.Address1, 3), _values[3]);
        snapshots[3] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Restore(Snapshot.EmptyPosition, snapshots[snapshot + 1], Snapshot.EmptyPosition);

        Assert.That(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[Math.Min(snapshot + 1, 1)]));
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
        Context ctx = new();
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
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        int[] snapshots = new int[4];
        snapshots[0] = Snapshot.EmptyPosition;
        provider.Get(new StorageCell(ctx.Address1, 1));
        provider.Get(new StorageCell(ctx.Address1, 1));
        provider.Get(new StorageCell(ctx.Address1, 1));
        provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
        snapshots[1] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
        snapshots[2] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
        snapshots[3] = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Restore(Snapshot.EmptyPosition, snapshots[2], Snapshot.EmptyPosition);
        provider.Restore(Snapshot.EmptyPosition, snapshots[1], Snapshot.EmptyPosition);
        provider.Restore(Snapshot.EmptyPosition, snapshots[0], Snapshot.EmptyPosition);
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
        Context ctx = new(setInitialState: false);
        // block 1
        Hash256 stateRoot;
        WorldState storageProvider = BuildStorageProvider(ctx);
        using (var _ = storageProvider.BeginScope(IWorldState.PreGenesis))
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
        using (var _ = storageProvider.BeginScope(newBase))
        {
            storageProvider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
            storageProvider.Commit(Frontier.Instance);
            storageProvider.CommitTree(0);
        }

        using (var _ = storageProvider.BeginScope(newBase))
        {
            storageProvider.AccountExists(ctx.Address1).Should().BeTrue();

            byte[] valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1)).ToArray();

            Assert.That(valueAfter, Is.EqualTo(_values[1]));
        }
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

        byte[] valueAfter = storageProvider.Get(new StorageCell(ctx.Address1, 1)).ToArray();
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
        Context ctx = new Context();
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

        provider.Restore(snapshots[snapshot + 1]);

        Assert.That(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).ToArray(), Is.EqualTo(_values[snapshot + 1]));
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
        Context ctx = new();
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
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        int requestedSnapshot = snapshot;
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

        _values[snapshot + 1].Should().BeEquivalentTo(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).ToArray());

        byte[] expectedPersistent = requestedSnapshot switch
        {
            -1 => _values[0],
            0 => _values[0],
            1 => _values[9],
            _ => _values[8],
        };
        expectedPersistent.Should().BeEquivalentTo(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray());
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
        int requestedSnapshot = snapshot;
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

        _values[snapshot + 1].Should().BeEquivalentTo(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray());

        byte[] expectedTransient = requestedSnapshot switch
        {
            -1 => _values[0],
            0 => _values[0],
            1 => _values[9],
            _ => _values[8],
        };
        expectedTransient.Should().BeEquivalentTo(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).ToArray());
    }

    /// <summary>
    /// Reset will reset transient state
    /// </summary>
    [Test]
    public void Selfdestruct_clears_cache()
    {
        PreBlockCaches preBlockCaches = new PreBlockCaches();
        Context ctx = new(preBlockCaches);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell accessedStorageCell = new StorageCell(TestItem.AddressA, 1);
        StorageCell nonAccessedStorageCell = new StorageCell(TestItem.AddressA, 2);
        preBlockCaches.StorageCache.Set(accessedStorageCell, [1, 2, 3]);
        provider.Get(accessedStorageCell);
        provider.Commit(Paris.Instance);
        provider.ClearStorage(TestItem.AddressA);
        provider.Get(accessedStorageCell).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);
        provider.Get(nonAccessedStorageCell).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);
    }

    [Test]
    public void Selfdestruct_works_across_blocks()
    {
        Context ctx = new(setInitialState: false, trackWrittenData: true);
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
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(provider.StateRoot).TestObject;
        }

        baseBlock.StateRoot.Should().NotBe(originalStateRoot);

        ctx.WrittenData.SelfDestructed[TestItem.AddressA].Should().BeTrue();
        ctx.WrittenData.Clear();

        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.ClearStorage(TestItem.AddressA);
            provider.Set(new StorageCell(TestItem.AddressA, 100), [1]);
            provider.Set(new StorageCell(TestItem.AddressA, 200), [2]);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(provider.StateRoot).TestObject;
        }

        baseBlock.StateRoot.Should().Be(originalStateRoot);

        ctx.WrittenData.SelfDestructed[TestItem.AddressA].Should().BeTrue();
    }

    [Test]
    public void Selfdestruct_works_even_when_its_the_only_call()
    {
        Context ctx = new(setInitialState: false, trackWrittenData: true);
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

        ctx.WrittenData.SelfDestructed[TestItem.AddressA].Should().BeTrue();
        ctx.WrittenData.Clear();

        using (provider.BeginScope(baseBlock))
        {
            provider.CreateAccountIfNotExists(TestItem.AddressA, 100);
            provider.Get(new StorageCell(TestItem.AddressA, 100)).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
        }
    }

    [Test]
    public void Selfdestruct_in_the_same_transaction()
    {
        Context ctx = new(setInitialState: false);
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

        baseBlock.StateRoot.Should().Be(Keccak.EmptyTreeHash);
    }

    [Test]
    public void Selfdestruct_before_commit_will_mark_contract_as_empty()
    {
        Context ctx = new(setInitialState: false);
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
        PreBlockCaches preBlockCaches = new PreBlockCaches();
        Context ctx = new(preBlockCaches);
        StorageCell accessedStorageCell = new StorageCell(TestItem.AddressA, 1);
        preBlockCaches.StorageCache.Set(accessedStorageCell, [1, 2, 3]);

        WorldState provider = BuildStorageProvider(ctx);
        provider.Get(accessedStorageCell).ToArray().Should().BeEquivalentTo([1, 2, 3]);
        provider.ClearStorage(TestItem.AddressA);
        provider.Commit(Paris.Instance);
        provider.Get(accessedStorageCell).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);
    }

    [Test]
    public void Eip161_empty_account_with_storage_does_not_throw_on_commit()
    {
        IWorldState worldState = new WorldState(
            new TrieStoreScopeProvider(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance), LogManager);

        using var disposable = worldState.BeginScope(IWorldState.PreGenesis);

        // Create an empty account (balance=0, nonce=0, no code) and set storage on it.
        // EIP-161 (via SpuriousDragon+) deletes empty accounts during commit, but the
        // storage flush has already produced a non-empty storage root. The commit must
        // handle this gracefully by skipping the storage root update for deleted accounts.
        worldState.CreateAccount(TestItem.AddressA, 0);
        worldState.Set(new StorageCell(TestItem.AddressA, 1), [1, 2, 3]);
        worldState.Commit(SpuriousDragon.Instance);

        worldState.AccountExists(TestItem.AddressA).Should().BeFalse();
    }

    [TestCase(2)]
    [TestCase(1000)]
    public void Set_empty_value_for_storage_cell_without_read_clears_data(int numItems)
    {
        IWorldState worldState = new WorldState(
            new TrieStoreScopeProvider(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance), LogManager);

        using var disposable = worldState.BeginScope(IWorldState.PreGenesis);
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

        var fullHash = worldState.StateRoot;
        fullHash.Should().NotBe(emptyHash);

        for (int i = 0; i < numItems; i++)
        {
            worldState.Set(new StorageCell(TestItem.AddressA, (UInt256)i), [0]);
        }
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(2);

        var clearedHash = worldState.StateRoot;

        clearedHash.Should().Be(emptyHash);
    }

    [Test]
    public void Set_empty_value_for_storage_cell_with_read_clears_data()
    {
        IWorldState worldState = new WorldState(
            new TrieStoreScopeProvider(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance), LogManager);

        using var disposable = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(TestItem.AddressA, 1);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(0);
        Hash256 emptyHash = worldState.StateRoot;

        worldState.Set(new StorageCell(TestItem.AddressA, 1), _values[11]);
        worldState.Set(new StorageCell(TestItem.AddressA, 2), _values[12]);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(1);

        var fullHash = worldState.StateRoot;
        fullHash.Should().NotBe(emptyHash);

        worldState.Get(new StorageCell(TestItem.AddressA, 1));
        worldState.Get(new StorageCell(TestItem.AddressA, 2));
        worldState.Set(new StorageCell(TestItem.AddressA, 1), [0]);
        worldState.Set(new StorageCell(TestItem.AddressA, 2), [0]);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(2);

        var clearedHash = worldState.StateRoot;

        clearedHash.Should().Be(emptyHash);
    }

    [Test]
    public void Commit_empty_account_with_storage_value_materializes_account()
    {
        IWorldState worldState = new WorldState(
            new TrieStoreScopeProvider(TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance), new MemDb(), LimboLogs.Instance), LogManager);

        BlockHeader baseBlock = null;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 0);
            worldState.Set(new StorageCell(TestItem.AddressA, 1), [1]);

            // Pre-EIP-161 empty accounts are not auto-deleted during commit.
            Assert.DoesNotThrow(() => worldState.Commit(Frontier.Instance));
            worldState.AccountExists(TestItem.AddressA).Should().BeTrue();

            worldState.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).WithNumber(0).TestObject;
        }

        using (worldState.BeginScope(baseBlock))
        {
            worldState.AccountExists(TestItem.AddressA).Should().BeTrue();
            worldState.Get(new StorageCell(TestItem.AddressA, 1)).ToArray().Should().BeEquivalentTo([1]);
        }
    }

    // --- 6.4.2 Regression: GetOriginal cross-transaction ---

    [Test]
    public void GetOriginal_returns_value_at_transaction_start_after_prior_commit()
    {
        // TX1 writes slot A=5, commits.
        // TX2 reads slot A (gets 5). TX2 writes slot A=7.
        // GetOriginal must return 5 (value at TX2 start), not 0 (on-disk value).
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        // TX1: write 5, commit
        provider.Set(cell, [5]);
        provider.Commit(Frontier.Instance);

        // TX2: take snapshot with newTransactionStart=true
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell); // read to populate _originalValues
        provider.Set(cell, [7]);

        byte[] original = provider.GetOriginal(cell);
        original.Should().BeEquivalentTo(new byte[] { 5 });
    }

    [Test]
    public void GetOriginal_returns_on_disk_value_when_prior_tx_reverted()
    {
        // TX1 writes slot A=5, TX1 reverts.
        // TX2 writes slot A=7.
        // GetOriginal must return 0 (on-disk value, TX1 was reverted).
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        // TX1: write 5, then revert
        Snapshot snap = provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell); // read first to populate _originalValues
        provider.Set(cell, [5]);
        provider.Restore(snap);

        // TX2: write 7
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell);
        provider.Set(cell, [7]);

        byte[] original = provider.GetOriginal(cell);
        original.Should().BeEquivalentTo(StorageTree.ZeroBytes);
    }

    [Test]
    public void GetOriginal_returns_on_disk_value_within_single_transaction()
    {
        // Within single TX: write A=5, write A=7.
        // GetOriginal must return 0 (on-disk value, since value-at-tx-start was 0).
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell); // read to populate _originalValues
        provider.Set(cell, [5]);
        provider.Set(cell, [7]);

        byte[] original = provider.GetOriginal(cell);
        original.Should().BeEquivalentTo(StorageTree.ZeroBytes);
    }

    // --- BuildUp mode: no commit between transactions (block production path) ---
    // In BuildUp mode there is no Commit between transactions. GetOriginal must still
    // return the correct value-at-transaction-start for EIP-2200 net gas metering.
    // The call sequence mirrors the real EVM: SLOAD → Get, then SSTORE → GetOriginal
    // (before Set). GetOriginal is always called BEFORE Set for the current SSTORE.

    [Test]
    public void GetOriginal_without_commit_returns_prior_tx_value()
    {
        // TX1 writes slot A=5. No commit.
        // TX2 reads A (gets 5), then SSTORE calls GetOriginal before Set.
        // GetOriginal must return 5 (value at TX2 start), not 0 (stale tree value).
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        // TX1: SLOAD + SSTORE
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell);
        provider.Set(cell, [5]);

        // TX2: no commit — new transaction boundary only
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell);

        // SSTORE sequence: GetOriginal is called BEFORE Set
        byte[] original = provider.GetOriginal(cell);
        original.Should().BeEquivalentTo(new byte[] { 5 });
        provider.Set(cell, [7]);
    }

    [Test]
    public void GetOriginal_without_commit_returns_tree_value_for_new_slot()
    {
        // TX1 writes slot A=5. No commit.
        // TX2 first accesses NEW slot B, writes B=10, then 2nd SSTORE writes B=20.
        // On the 2nd SSTORE, GetOriginal(B) must return 0 (tree value),
        // not 10 (current _slotsHot value after 1st SSTORE).
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cellA = new(ctx.Address1, 1);
        StorageCell cellB = new(ctx.Address1, 2);

        // TX1: write slot A
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cellA);
        provider.Set(cellA, [5]);

        // TX2: first access slot B, 1st SSTORE writes 10
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cellB);
        provider.Set(cellB, [10]);

        // 2nd SSTORE on B: GetOriginal before Set
        byte[] original = provider.GetOriginal(cellB);
        original.Should().BeEquivalentTo(StorageTree.ZeroBytes);
        provider.Set(cellB, [20]);
    }

    [Test]
    public void GetOriginal_without_commit_handles_both_slot_types()
    {
        // Combined: TX1 writes A=5. No commit.
        // TX2: SSTORE A=7 (GetOriginal(A) must be 5),
        //      1st SSTORE B=10, 2nd SSTORE B=20 (GetOriginal(B) must be 0).
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cellA = new(ctx.Address1, 1);
        StorageCell cellB = new(ctx.Address1, 2);

        // TX1
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cellA);
        provider.Set(cellA, [5]);

        // TX2 — no commit
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cellA);
        provider.Get(cellB);

        // SSTORE A: GetOriginal before Set
        provider.GetOriginal(cellA).Should().BeEquivalentTo(new byte[] { 5 });
        provider.Set(cellA, [7]);

        // 1st SSTORE B
        provider.Set(cellB, [10]);

        // 2nd SSTORE B: GetOriginal before Set
        provider.GetOriginal(cellB).Should().BeEquivalentTo(StorageTree.ZeroBytes);
        provider.Set(cellB, [20]);
    }

    // --- 6.4.2 Regression: nested SSTORE revert ---

    [Test]
    public void Nested_sstore_revert_restores_value()
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.Set(cell, [1]);
        Snapshot snap = provider.TakeSnapshot();
        provider.Set(cell, [2]);
        Snapshot snap2 = provider.TakeSnapshot();
        provider.Set(cell, [3]);

        // Revert inner
        provider.Restore(snap2);
        provider.Get(cell).ToArray().Should().BeEquivalentTo(new byte[] { 2 });

        // Revert outer
        provider.Restore(snap);
        provider.Get(cell).ToArray().Should().BeEquivalentTo(new byte[] { 1 });
    }

    // --- 6.4.2 Regression: TSTORE revert ---

    [Test]
    public void Nested_tstore_revert_restores_value()
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.SetTransientState(cell, [1]);
        Snapshot snap = provider.TakeSnapshot();
        provider.SetTransientState(cell, [2]);
        Snapshot snap2 = provider.TakeSnapshot();
        provider.SetTransientState(cell, [3]);

        // Revert inner
        provider.Restore(snap2);
        provider.GetTransientState(cell).ToArray().Should().BeEquivalentTo(new byte[] { 2 });

        // Revert outer
        provider.Restore(snap);
        provider.GetTransientState(cell).ToArray().Should().BeEquivalentTo(new byte[] { 1 });
    }

    // --- 6.4.3 Regression: read tracing ---

    [Test]
    public void Read_tracing_reports_exact_read_and_changed_sets()
    {
        // Exact-set assertions: read-only cells appear via ReportStorageRead,
        // written cells via ReportStorageChange — no overlap, no extras.
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell readCell = new(ctx.Address1, 1);
        StorageCell writeCell = new(ctx.Address1, 2);

        provider.Get(readCell);       // read only
        provider.Set(writeCell, [5]); // write only (no prior read)

        TestStorageTracer tracer = new();
        provider.Commit(Frontier.Instance, tracer);

        tracer.ReadCells.Should().BeEquivalentTo(new[] { readCell });
        tracer.ChangedCells.Should().HaveCount(1);
        tracer.ChangedCells.Should().ContainKey(writeCell);
    }

    [Test]
    public void Read_tracing_does_not_report_written_cells_as_reads()
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.Get(cell);      // read first
        provider.Set(cell, [5]); // then write — promotes from Read to Dirty

        TestStorageTracer tracer = new();
        provider.Commit(Frontier.Instance, tracer);

        // Cell was written, so it appears in ChangedCells only
        tracer.ChangedCells.Should().ContainKey(cell);
        tracer.ReadCells.Should().BeEmpty();
    }

    [Test]
    public void Read_tracing_hash_key_cells_bypass_slot_cache_and_tracing()
    {
        // Hash-key reads (StorageCell with IsHash=true) must NOT create slots,
        // so they should NOT appear in either ReportStorageRead or ReportStorageChange.
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell normalCell = new(ctx.Address1, 1);
        ValueHash256 hash = new(Keccak.Compute("test").ValueHash256.Bytes);
        StorageCell hashCell = new(ctx.Address1, hash);

        provider.Get(normalCell); // normal read — should be traced
        provider.Get(hashCell);   // hash-key read — should bypass tracing

        TestStorageTracer tracer = new();
        provider.Commit(Frontier.Instance, tracer);

        tracer.ReadCells.Should().BeEquivalentTo(new[] { normalCell });
        tracer.ChangedCells.Should().BeEmpty();
    }

    [Test]
    public void Read_tracing_after_restore_past_read_then_write()
    {
        // Edge case: read → restore past the read → write same cell.
        // The read slot gets reverted (flags cleared back to pre-read state).
        // The subsequent write creates a fresh dirty entry.
        // The cell should appear in ChangedCells, not ReadCells.
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        Snapshot snap = provider.TakeSnapshot();
        provider.Get(cell); // read — creates slot with Read flag
        provider.Restore(snap); // undo restores slot to pre-read state

        provider.Set(cell, [5]); // fresh write after restore

        TestStorageTracer tracer = new();
        provider.Commit(Frontier.Instance, tracer);

        tracer.ChangedCells.Should().ContainKey(cell);
        tracer.ReadCells.Should().BeEmpty();
    }

    // --- 6.4.4 Regression: ClearStorage + revert ---

    [Test]
    public void ClearStorage_zeros_all_cached_cells()
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cellA = new(ctx.Address1, 1);
        StorageCell cellB = new(ctx.Address1, 2);
        StorageCell cellC = new(ctx.Address1, 3);

        provider.Set(cellA, [1]);
        provider.Set(cellB, [2]);
        provider.Get(cellC); // read only

        provider.ClearStorage(ctx.Address1);

        provider.Get(cellA).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);
        provider.Get(cellB).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);
        provider.Get(cellC).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);
    }

    [Test]
    public void ClearStorage_revert_restores_written_values()
    {
        Context ctx = new();
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cellA = new(ctx.Address1, 1);
        StorageCell cellB = new(ctx.Address1, 2);

        provider.Set(cellA, [1]);
        provider.Set(cellB, [2]);

        Snapshot snap = provider.TakeSnapshot();
        provider.ClearStorage(ctx.Address1);

        provider.Get(cellA).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);

        provider.Restore(snap);

        provider.Get(cellA).ToArray().Should().BeEquivalentTo(new byte[] { 1 });
        provider.Get(cellB).ToArray().Should().BeEquivalentTo(new byte[] { 2 });
    }

    [Test]
    public void ClearStorage_revert_restores_nonzero_read_only_tree_values()
    {
        // Pre-populate storage with non-zero values in the tree, then in a new
        // scope read a cell (creating a read-only slot), ClearStorage, and revert.
        // The read-only cell must be restored to its non-zero tree value.
        Context ctx = new(setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cellA = new(ctx.Address1, 1);
        StorageCell cellB = new(ctx.Address1, 2);

        // Block 0: persist non-zero values to tree
        BlockHeader baseBlock;
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            provider.CreateAccount(ctx.Address1, 0);
            provider.Set(cellA, [10]);
            provider.Set(cellB, [20]);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        // Block 1: read cellB (non-zero from tree), write cellA, ClearStorage, revert
        using (provider.BeginScope(baseBlock))
        {
            provider.Set(cellA, [99]);
            provider.Get(cellB).ToArray().Should().BeEquivalentTo(new byte[] { 20 }); // read-only, non-zero

            Snapshot snap = provider.TakeSnapshot();
            provider.ClearStorage(ctx.Address1);

            provider.Get(cellA).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);
            provider.Get(cellB).ToArray().Should().BeEquivalentTo(StorageTree.ZeroBytes);

            provider.Restore(snap);

            // Written cell restored to pre-clear value
            provider.Get(cellA).ToArray().Should().BeEquivalentTo(new byte[] { 99 });
            // Read-only cell restored to non-zero tree value — not zero
            provider.Get(cellB).ToArray().Should().BeEquivalentTo(new byte[] { 20 });
        }
    }

    // --- Test tracer for 6.4.3 ---

    private class TestStorageTracer : IWorldStateTracer
    {
        public HashSet<StorageCell> ReadCells { get; } = new();
        public Dictionary<StorageCell, (byte[] Before, byte[] After)> ChangedCells { get; } = new();

        public bool IsTracingState => true;
        public bool IsTracingStorage => true;

#nullable enable
        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) { }
        public void ReportCodeChange(Address address, byte[]? before, byte[]? after) { }
        public void ReportNonceChange(Address address, UInt256? before, UInt256? after) { }
#nullable restore
        public void ReportAccountRead(Address address) { }
        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) { }
        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
            => ChangedCells[storageCell] = (before, after);
        public void ReportStorageRead(in StorageCell storageCell) => ReadCells.Add(storageCell);
    }

    private class Context
    {
        public WorldState StateProvider { get; }
        internal WrittenData WrittenData = null;

        public readonly Address Address1 = new(Keccak.Compute("1"));
        public readonly Address Address2 = new(Keccak.Compute("2"));

        public Context(PreBlockCaches preBlockCaches = null, bool setInitialState = true, bool trackWrittenData = false)
        {
            IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
                TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
                new MemDb(), LimboLogs.Instance);

            if (preBlockCaches is not null)
            {
                scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: true);
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

        public bool HasRoot(BlockHeader baseBlock)
        {
            return scopeProvider.HasRoot(baseBlock);
        }

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader baseBlock)
        {
            return new ScopeDecorator(scopeProvider.BeginScope(baseBlock), writtenData);
        }

        private class ScopeDecorator(IWorldStateScopeProvider.IScope baseScope, WrittenData writtenData) : IWorldStateScopeProvider.IScope
        {
            public void Dispose()
            {
                baseScope.Dispose();
            }

            public Hash256 RootHash => baseScope.RootHash;

            public void UpdateRootHash()
            {
                baseScope.UpdateRootHash();
            }

            public Account Get(Address address)
            {
                return baseScope.Get(address);
            }

            public void HintGet(Address address, Account account)
            {
                baseScope.HintGet(address, account);
            }

            public IWorldStateScopeProvider.ICodeDb CodeDb => baseScope.CodeDb;

            public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
            {
                return baseScope.CreateStorageTree(address);
            }

            public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
            {
                return new WriteBatchDecorator(baseScope.StartWriteBatch(estimatedAccountNum), writtenData);
            }

            public void Commit(long blockNumber)
            {
                baseScope.Commit(blockNumber);
            }
        }

        private class WriteBatchDecorator(
            IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch,
            WrittenData writtenData
        )
            : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public void Dispose()
            {
                writeBatch.Dispose();
            }

            public event EventHandler<IWorldStateScopeProvider.AccountUpdated> OnAccountUpdated
            {
                add => writeBatch.OnAccountUpdated += value;
                remove => writeBatch.OnAccountUpdated -= value;
            }

            public void Set(Address key, Account account)
            {
                writeBatch.Set(key, account);
            }

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
            {
                return new StorageWriteBatchDecorator(writeBatch.CreateStorageWriteBatch(key, estimatedEntries), key, writtenData);

            }
        }

        private class StorageWriteBatchDecorator(
            IWorldStateScopeProvider.IStorageWriteBatch baseStorageBatch,
            Address address,
            WrittenData writtenData
        ) : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Dispose()
            {
                baseStorageBatch?.Dispose();
            }

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
}
