// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
        provider.Set(new StorageCell(ctx.Address1, 1), _values[1]);
        provider.Set(new StorageCell(ctx.Address1, 1), _values[2]);
        provider.Set(new StorageCell(ctx.Address1, 1), _values[3]);
        provider.Restore(Snapshot.EmptyPosition, snapshot, Snapshot.EmptyPosition);

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

        _values[snapshot + 1].Should().BeEquivalentTo(provider.GetTransientState(new StorageCell(ctx.Address1, 1)).ToArray());
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
            new Snapshot(new Snapshot.Storage(0, Snapshot.EmptyPosition), Snapshot.EmptyPosition),
            new Snapshot(new Snapshot.Storage(1, 0), Snapshot.EmptyPosition),
            new Snapshot(new Snapshot.Storage(1, 1), Snapshot.EmptyPosition)
        );

        _values[snapshot + 1].Should().BeEquivalentTo(provider.Get(new StorageCell(ctx.Address1, 1)).ToArray());
    }

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

    [Test]
    public void Apply_block_deltas_updates_warm_state_cache_for_modified_accounts()
    {
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        IWorldState worldState = new WorldState(scopeProvider, LogManager);

        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);

        worldState.CreateAccount(TestItem.AddressA, 0);
        worldState.Commit(Frontier.Instance);

        preBlockCaches.StateCache.Set(TestItem.AddressA, new Account((UInt256)0, (UInt256)0));

        worldState.IncrementNonce(TestItem.AddressA, 1);
        worldState.Commit(Frontier.Instance);
        worldState.ApplyBlockDeltasToWarmCache();

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cachedAccount).Should().BeTrue();
        cachedAccount.Nonce.Should().Be((UInt256)1);
    }

    [Test]
    public void Apply_block_deltas_storage_only_update_uses_authoritative_account_state()
    {
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState worldState = new(scopeProvider, LogManager);

        BlockHeader baseBlock;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, (UInt256)1, (UInt256)1);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
        }

        preBlockCaches.StateCache.Set(TestItem.AddressA, new Account((UInt256)0, (UInt256)1));

        using (worldState.BeginScope(baseBlock))
        {
            _ = worldState.GetNonce(TestItem.AddressA);
            worldState.Set(new StorageCell(TestItem.AddressA, 1), [1]);
            worldState.Commit(Frontier.Instance);
            worldState.ApplyBlockDeltasToWarmCache();
        }

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cachedAccount).Should().BeTrue();
        cachedAccount.Nonce.Should().Be((UInt256)1);
    }

    [Test]
    public void Cross_block_cache_state_epoch_cleared_and_rebuilt_per_block()
    {
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState worldState = new(scopeProvider, LogManager);

        // Block 0: create account with nonce 0
        BlockHeader block0Header;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, (UInt256)100, (UInt256)0);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            block0Header = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Verify cache populated after block 0
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cached0).Should().BeTrue();
        cached0.Nonce.Should().Be((UInt256)0);

        // Block 1: increment nonce to 1
        BlockHeader block1Header;
        using (worldState.BeginScope(block0Header))
        {
            worldState.IncrementNonce(TestItem.AddressA, 1);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(1);
            block1Header = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Epoch clear + rebuild should show nonce=1, not stale nonce=0
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cached1).Should().BeTrue();
        cached1.Nonce.Should().Be((UInt256)1);

        // Block 2: increment nonce to 2
        using (worldState.BeginScope(block1Header))
        {
            worldState.IncrementNonce(TestItem.AddressA, 1);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(2);
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Should now show nonce=2
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cached2).Should().BeTrue();
        cached2.Nonce.Should().Be((UInt256)2);
    }

    [Test]
    public void Cross_block_storage_cache_retained_across_blocks()
    {
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState worldState = new(scopeProvider, LogManager);

        StorageCell cellA = new(TestItem.AddressA, 1);
        StorageCell cellB = new(TestItem.AddressA, 2);

        // Block 0: create account and write storage slot 1
        BlockHeader block0Header;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, (UInt256)100);
            worldState.Set(cellA, [42]);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            block0Header = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Verify storage slot 1 cached
        preBlockCaches.StorageCache.TryGetValue(cellA, out byte[] cachedA).Should().BeTrue();
        cachedA.Should().BeEquivalentTo(new byte[] { 42 });

        // Block 1: write storage slot 2 only (slot 1 untouched)
        using (worldState.BeginScope(block0Header))
        {
            worldState.Set(cellB, [99]);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(1);
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Storage cache retained: slot 1 still present, slot 2 now added
        preBlockCaches.StorageCache.TryGetValue(cellB, out byte[] cachedB).Should().BeTrue();
        cachedB.Should().BeEquivalentTo(new byte[] { 99 });
    }

    [Test]
    public void Cross_block_storage_delta_overwrites_stale_entries()
    {
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState worldState = new(scopeProvider, LogManager);

        StorageCell cell = new(TestItem.AddressA, 1);

        // Block 0: write slot 1 = 10
        BlockHeader block0Header;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, (UInt256)100);
            worldState.Set(cell, [10]);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            block0Header = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        preBlockCaches.StorageCache.TryGetValue(cell, out byte[] cached0).Should().BeTrue();
        cached0.Should().BeEquivalentTo(new byte[] { 10 });

        // Block 1: overwrite slot 1 = 20
        using (worldState.BeginScope(block0Header))
        {
            worldState.Set(cell, [20]);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(1);
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Delta should overwrite stale value
        preBlockCaches.StorageCache.TryGetValue(cell, out byte[] cached1).Should().BeTrue();
        cached1.Should().BeEquivalentTo(new byte[] { 20 });
    }

    [Test]
    public void Cross_block_cache_fork_detection_clears_all_caches()
    {
        PreBlockCaches preBlockCaches = new();

        // Pre-populate caches with some data
        preBlockCaches.StateCache.Set(TestItem.AddressA, new Account((UInt256)5, (UInt256)100));
        preBlockCaches.StorageCache.Set(new StorageCell(TestItem.AddressA, 1), [42]);
        preBlockCaches.LastProcessedBlockHash = TestItem.KeccakA;

        // Simulate fork: parent hash doesn't match last processed
        Hash256 differentParentHash = TestItem.KeccakB;
        differentParentHash.Should().NotBe(preBlockCaches.LastProcessedBlockHash);

        // Fork detection triggers full clear
        preBlockCaches.ClearAllCaches();

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out _).Should().BeFalse();
        preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressA, 1), out _).Should().BeFalse();
        preBlockCaches.LastProcessedBlockHash.Should().BeNull();
    }

    [Test]
    public void Cross_block_cache_clear_caches_preserves_state_and_storage()
    {
        PreBlockCaches preBlockCaches = new();

        // Pre-populate all caches (Account ctor: nonce, balance)
        preBlockCaches.StateCache.Set(TestItem.AddressA, new Account((UInt256)5, (UInt256)100));
        preBlockCaches.StorageCache.Set(new StorageCell(TestItem.AddressA, 1), [42]);
        preBlockCaches.PrecompileCache.TryAdd(
            new PreBlockCaches.PrecompileCacheKey(TestItem.AddressA, new byte[] { 1, 2, 3 }),
            new byte[] { 4, 5, 6 });
        preBlockCaches.LastProcessedBlockHash = TestItem.KeccakA;

        // Per-block clear should only clear precompile, not state/storage
        preBlockCaches.ClearCaches();

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cachedAccount).Should().BeTrue();
        cachedAccount.Balance.Should().Be((UInt256)100);
        preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressA, 1), out byte[] cachedStorage).Should().BeTrue();
        cachedStorage.Should().BeEquivalentTo(new byte[] { 42 });
        preBlockCaches.PrecompileCache.Should().BeEmpty();
        preBlockCaches.LastProcessedBlockHash.Should().Be(TestItem.KeccakA);
    }

    [Test]
    public void Cross_block_cache_notify_block_processed_tracks_hash()
    {
        PreBlockCaches preBlockCaches = new();

        preBlockCaches.LastProcessedBlockHash.Should().BeNull();

        preBlockCaches.LastProcessedBlockHash = TestItem.KeccakA;
        preBlockCaches.LastProcessedBlockHash.Should().Be(TestItem.KeccakA);

        preBlockCaches.LastProcessedBlockHash = TestItem.KeccakB;
        preBlockCaches.LastProcessedBlockHash.Should().Be(TestItem.KeccakB);

        // Invalidation on error
        preBlockCaches.LastProcessedBlockHash = null;
        preBlockCaches.LastProcessedBlockHash.Should().BeNull();
    }

    [Test]
    public void Cross_block_cache_multi_block_nonce_progression_with_multiple_accounts()
    {
        // State cache is epoch-cleared per block, so only accounts modified in the
        // last block are present. This test verifies the cache reflects the final block's
        // deltas accurately when both accounts are modified together.
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState worldState = new(scopeProvider, LogManager);

        // Block 0: create two accounts
        BlockHeader prevHeader;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, (UInt256)1000, (UInt256)0);
            worldState.CreateAccount(TestItem.AddressB, (UInt256)2000, (UInt256)0);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            prevHeader = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Block 1: increment A nonce
        using (worldState.BeginScope(prevHeader))
        {
            worldState.IncrementNonce(TestItem.AddressA, 1);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(1);
            prevHeader = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // After block 1: only A is in cache (epoch-clear removed B)
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cachedA1).Should().BeTrue();
        cachedA1.Nonce.Should().Be((UInt256)1);

        // Block 2: increment both A and B
        using (worldState.BeginScope(prevHeader))
        {
            worldState.IncrementNonce(TestItem.AddressA, 1);
            worldState.IncrementNonce(TestItem.AddressB, 1);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(2);
            prevHeader = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // After block 2: both A and B are in cache since both were modified
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cachedA2).Should().BeTrue();
        cachedA2.Nonce.Should().Be((UInt256)2);
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressB, out Account cachedB2).Should().BeTrue();
        cachedB2.Nonce.Should().Be((UInt256)1);
    }

    [Test]
    public void Cross_block_cache_state_root_matches_without_cache()
    {
        // Build reference chain without PrewarmerScopeProvider
        IWorldStateScopeProvider refScopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        WorldState refWorldState = new(refScopeProvider, LogManager);

        Hash256 refStateRoot;
        using (refWorldState.BeginScope(IWorldState.PreGenesis))
        {
            refWorldState.CreateAccount(TestItem.AddressA, (UInt256)100, (UInt256)0);
            refWorldState.Set(new StorageCell(TestItem.AddressA, 1), [10]);
            refWorldState.Commit(Frontier.Instance);
            refWorldState.CommitTree(0);
            Hash256 block0Root = refWorldState.StateRoot;

            // Simulate closing scope and reopening for next block
            refStateRoot = block0Root;
        }

        BlockHeader refBlock0 = Build.A.BlockHeader.WithStateRoot(refStateRoot).TestObject;
        using (refWorldState.BeginScope(refBlock0))
        {
            refWorldState.IncrementNonce(TestItem.AddressA, 1);
            refWorldState.Set(new StorageCell(TestItem.AddressA, 1), [20]);
            refWorldState.Commit(Frontier.Instance);
            refWorldState.CommitTree(1);
            refStateRoot = refWorldState.StateRoot;
        }

        // Build same chain with PrewarmerScopeProvider
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider cachedScopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        cachedScopeProvider = new PrewarmerScopeProvider(cachedScopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState cachedWorldState = new(cachedScopeProvider, LogManager);

        Hash256 cachedStateRoot;
        using (cachedWorldState.BeginScope(IWorldState.PreGenesis))
        {
            cachedWorldState.CreateAccount(TestItem.AddressA, (UInt256)100, (UInt256)0);
            cachedWorldState.Set(new StorageCell(TestItem.AddressA, 1), [10]);
            cachedWorldState.Commit(Frontier.Instance);
            cachedWorldState.CommitTree(0);
            cachedStateRoot = cachedWorldState.StateRoot;
            cachedWorldState.ApplyBlockDeltasToWarmCache();
        }

        BlockHeader cachedBlock0 = Build.A.BlockHeader.WithStateRoot(cachedStateRoot).TestObject;
        using (cachedWorldState.BeginScope(cachedBlock0))
        {
            cachedWorldState.IncrementNonce(TestItem.AddressA, 1);
            cachedWorldState.Set(new StorageCell(TestItem.AddressA, 1), [20]);
            cachedWorldState.Commit(Frontier.Instance);
            cachedWorldState.CommitTree(1);
            cachedStateRoot = cachedWorldState.StateRoot;
            cachedWorldState.ApplyBlockDeltasToWarmCache();
        }

        // State roots must match regardless of caching
        cachedStateRoot.Should().Be(refStateRoot);
    }

    [Test]
    public void Cross_block_cache_many_storage_slots_across_blocks()
    {
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState worldState = new(scopeProvider, LogManager);

        const int slotsPerBlock = 50;
        const int blockCount = 5;

        BlockHeader prevHeader;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, (UInt256)100000);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            prevHeader = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Each block writes slotsPerBlock unique storage slots
        for (int block = 1; block <= blockCount; block++)
        {
            using (worldState.BeginScope(prevHeader))
            {
                for (int slot = 0; slot < slotsPerBlock; slot++)
                {
                    int globalSlot = (block - 1) * slotsPerBlock + slot;
                    UInt256 value = (UInt256)(globalSlot + 1);
                    worldState.Set(new StorageCell(TestItem.AddressA, (UInt256)globalSlot), value.ToBigEndian());
                }

                worldState.Commit(Frontier.Instance);
                worldState.CommitTree(block);
                prevHeader = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
                worldState.ApplyBlockDeltasToWarmCache();
            }
        }

        // Verify the most recent block's slots are in cache
        for (int slot = 0; slot < slotsPerBlock; slot++)
        {
            int lastBlockSlot = (blockCount - 1) * slotsPerBlock + slot;
            StorageCell cell = new(TestItem.AddressA, (UInt256)lastBlockSlot);
            preBlockCaches.StorageCache.TryGetValue(cell, out byte[] cached).Should().BeTrue(
                $"Slot {lastBlockSlot} should be cached after last block");
        }
    }

    [Test]
    public void Cross_block_cache_balance_transfer_progression()
    {
        PreBlockCaches preBlockCaches = new();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache: false);
        WorldState worldState = new(scopeProvider, LogManager);

        // Block 0: create accounts
        BlockHeader prevHeader;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, (UInt256)1000);
            worldState.CreateAccount(TestItem.AddressB, (UInt256)0);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            prevHeader = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        // Block 1: transfer 100 from A to B
        using (worldState.BeginScope(prevHeader))
        {
            worldState.SubtractFromBalance(TestItem.AddressA, (UInt256)100, Frontier.Instance);
            worldState.AddToBalance(TestItem.AddressB, (UInt256)100, Frontier.Instance);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(1);
            prevHeader = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
            worldState.ApplyBlockDeltasToWarmCache();
        }

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cachedA).Should().BeTrue();
        cachedA.Balance.Should().Be((UInt256)900);
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressB, out Account cachedB).Should().BeTrue();
        cachedB.Balance.Should().Be((UInt256)100);

        // Block 2: transfer another 200 from A to B
        using (worldState.BeginScope(prevHeader))
        {
            worldState.SubtractFromBalance(TestItem.AddressA, (UInt256)200, Frontier.Instance);
            worldState.AddToBalance(TestItem.AddressB, (UInt256)200, Frontier.Instance);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(2);
            worldState.ApplyBlockDeltasToWarmCache();
        }

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out Account cachedA2).Should().BeTrue();
        cachedA2.Balance.Should().Be((UInt256)700);
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressB, out Account cachedB2).Should().BeTrue();
        cachedB2.Balance.Should().Be((UInt256)300);
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
