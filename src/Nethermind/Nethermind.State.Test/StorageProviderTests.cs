// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
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
using CoreCollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

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
    public void Mixed_storage_deletes_updates_and_inserts_survive_persistence()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        BlockHeader baseBlock;
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            provider.CreateAccount(TestItem.AddressA, 100);
            for (uint slot = 1; slot <= 3; slot++)
                provider.Set(new StorageCell(TestItem.AddressA, slot), (UInt256)(slot + 6));
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            provider.Set(new StorageCell(TestItem.AddressA, 1), UInt256.Zero);
            provider.Set(new StorageCell(TestItem.AddressA, 2), UInt256.Zero);
            provider.Set(new StorageCell(TestItem.AddressA, 3), (UInt256)10);
            provider.Set(new StorageCell(TestItem.AddressA, 4), (UInt256)11);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(1);
            baseBlock = Build.A.BlockHeader.WithNumber(1).WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            uint[] expected = [0, 0, 10, 11];
            using (Assert.EnterMultipleScope())
            {
                for (uint slot = 1; slot <= expected.Length; slot++)
                {
                    provider.Get(new StorageCell(TestItem.AddressA, slot), out UInt256 value);
                    Assert.That(value, Is.EqualTo((UInt256)expected[slot - 1]), $"slot {slot}");
                }
            }
        }
    }

    [Test]
    public void Empty_commit_restore()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Commit(Frontier.Instance);
        provider.Restore(Snapshot.Empty);
    }

    private WorldState BuildStorageProvider(Context ctx) => ctx.StateProvider;

    [Test]
    public void Storage_access_after_scope_disposal_throws()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell storageCell = new(ctx.Address1, UInt256.Zero);
        IDisposable scope = provider.BeginScope(IWorldState.PreGenesis);
        scope.Dispose();

        Assert.That(
            () => provider.Set(in storageCell, new UInt256(_values[1], isBigEndian: true)),
            Throws.InvalidOperationException);

        using IDisposable nextScope = provider.BeginScope(IWorldState.PreGenesis);
        provider.Get(in storageCell, out UInt256 storageValue1);
        Assert.That(storageValue1.IsZero, Is.True);
    }

    [Test]
    public void Oversized_per_contract_state_dictionary_is_trimmed_on_reset([Values(1_024, 16_384)] int changeCount)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        for (int i = 0; i < changeCount; i++)
        {
            provider.Set(new StorageCell(ctx.Address1, (UInt256)i), new UInt256(_values[1], isBigEndian: true));
        }

        provider.Commit(Frontier.Instance);
        object blockChange = GetBlockChange(provider, ctx.Address1);
        int capacityBeforeReset = GetCapacity(blockChange);

        // Exercise the pool's reset while we still own the state; returned objects can be rented by background work.
        blockChange.GetType().GetMethod(nameof(provider.Reset))!.Invoke(blockChange, [512]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capacityBeforeReset, Is.GreaterThan(512));
            Assert.That(GetCapacity(blockChange), Is.GreaterThan(0));
            Assert.That(GetCapacity(blockChange), Is.LessThan(capacityBeforeReset));
            Assert.That(((IDictionary)GetDictionary(blockChange)).Count, Is.Zero);
        }
    }

    [Test]
    public void Reset_trims_oversized_round_collections()
    {
        const int OversizedCapacity = CoreCollectionExtensions.DefaultTrimAboveCapacity + 1;

        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        object[] collections =
        [
            GetPrivateField(provider._stateProvider, "_intraTxCache"),
            GetPrivateField(provider._stateProvider, "_committedThisRound"),
            GetPrivateField(provider._stateProvider, "_nullAccountReads"),
            GetPrivateField(provider._persistentStorageProvider, "_originalValues"),
            GetPrivateField(provider._persistentStorageProvider, "_destroyedThisRound"),
        ];
        int[] capacitiesBeforeReset = new int[collections.Length];
        for (int i = 0; i < collections.Length; i++)
        {
            EnsureCapacity(collections[i], OversizedCapacity);
            capacitiesBeforeReset[i] = GetCollectionCapacity(collections[i]);
        }

        provider.Reset();

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < collections.Length; i++)
            {
                Assert.That(capacitiesBeforeReset[i], Is.GreaterThan(CoreCollectionExtensions.DefaultTrimAboveCapacity));
                Assert.That(GetCollectionCapacity(collections[i]), Is.GreaterThan(0));
                Assert.That(GetCollectionCapacity(collections[i]), Is.LessThan(capacitiesBeforeReset[i]));
            }
        }
    }

    private static object GetBlockChange(WorldState provider, Address address)
    {
        FieldInfo storagesField = typeof(PersistentStorageProvider).GetField(
            "_storages",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IDictionary storages = (IDictionary)storagesField.GetValue(provider._persistentStorageProvider)!;
        object contractState = storages[new AddressAsKey(address)]
            ?? throw new InvalidOperationException("Contract state was not created.");
        FieldInfo blockChangeField = contractState.GetType().GetField(
            "BlockChange",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return blockChangeField.GetValue(contractState)!;
    }

    private static int GetCapacity(object collection)
    {
        object dictionary = GetDictionary(collection);
        return (int)dictionary.GetType().GetProperty(nameof(System.Collections.Generic.Dictionary<,>.Capacity))!.GetValue(dictionary)!;
    }

    private static object GetDictionary(object collection)
    {
        FieldInfo dictionaryField = collection.GetType().GetField(
            "_dictionary",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return dictionaryField.GetValue(collection)!;
    }

    private static object GetPrivateField(object owner, string fieldName) =>
        owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void EnsureCapacity(object collection, int capacity) =>
        collection.GetType().GetMethod(nameof(System.Collections.Generic.Dictionary<,>.EnsureCapacity), [typeof(int)])!.Invoke(collection, [capacity]);

    private static int GetCollectionCapacity(object collection) =>
        (int)collection.GetType().GetProperty(nameof(System.Collections.Generic.Dictionary<,>.Capacity))!.GetValue(collection)!;

    [Test]
    public void Same_address_same_index_different_values_restore([Range(-1, 2)] int snapshot)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        Snapshot[] snapshots = new Snapshot[4];
        snapshots[0] = provider.TakeSnapshot();
        for (int i = 1; i < snapshots.Length; i++)
        {
            provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[i], isBigEndian: true));
            snapshots[i] = provider.TakeSnapshot();
        }
        provider.Restore(snapshots[snapshot + 1]);

        provider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue2);
        Assert.That(storageValue2, Is.EqualTo(new UInt256(_values[snapshot + 1], isBigEndian: true)));
    }

    [Test]
    public void Keep_in_cache()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        provider.Commit(Frontier.Instance);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        provider.Restore(Snapshot.EmptyPosition, -1, Snapshot.EmptyPosition);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        provider.Restore(Snapshot.EmptyPosition, -1, Snapshot.EmptyPosition);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        provider.Restore(Snapshot.EmptyPosition, -1, Snapshot.EmptyPosition);
        provider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue3);
        Assert.That(storageValue3, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));
    }

    [Test]
    public void Original_value_tracks_transaction_start_across_stacked_writes()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        // tx0: capture the block original (zero), then write; changes stay uncommitted (BuildUp stacking).
        provider.TakeSnapshot(newTransactionStart: true);
        provider.Get(cell, out _);
        provider.Set(cell, new UInt256(_values[1], isBigEndian: true));

        // tx1 stacks on tx0. Its original is the value entering tx1 (_values[1]) and must stay stable
        // across repeated same-slot writes (the case the removed chain walk resolved in O(N^2)).
        provider.TakeSnapshot(newTransactionStart: true);
        provider.GetOriginal(in cell, out UInt256 originalValue);
        Assert.That(originalValue, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));
        for (int i = 2; i <= 6; i++)
        {
            provider.Set(cell, new UInt256(_values[i], isBigEndian: true));
            provider.GetOriginal(in cell, out originalValue);
            Assert.That(originalValue, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));
        }

        // A revert within tx1 must leave the transaction original unchanged.
        int mid = provider.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot;
        provider.Set(cell, new UInt256(_values[7], isBigEndian: true));
        provider.Restore(Snapshot.EmptyPosition, mid, Snapshot.EmptyPosition);
        provider.GetOriginal(in cell, out originalValue);
        Assert.That(originalValue, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));
    }

    [Test]
    public void Original_value_requires_capture_even_when_a_write_head_exists([Values] bool writeFirst, [Values(0ul, 1ul, ulong.MaxValue)] ulong index)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, index);
        StorageCell other = new(ctx.Address1, index ^ 1);
        provider.Get(in other, out _);
        if (writeFirst) provider.Set(in cell, UInt256.One);

        Assert.Throws<InvalidOperationException>(() => provider.GetOriginal(in cell, out _));
    }

    [Test]
    public void Original_value_cache_ends_with_the_capture_round([Values] bool write, [Values] bool reset, [Values(2UL, (ulong)uint.MaxValue << 1)] ulong round)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        typeof(PersistentStorageProvider).GetField("_originalsRound", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(provider._persistentStorageProvider, round);
        StorageCell cell = new(ctx.Address1, 1);
        provider.Get(in cell, out _);
        provider.GetOriginal(in cell, out UInt256 original);
        Assert.That(original, Is.EqualTo(UInt256.Zero));
        if (write) provider.Set(in cell, UInt256.One);

        if (reset) provider.Reset(resetBlockChanges: false);
        else provider.Commit(Frontier.Instance);

        Assert.Throws<InvalidOperationException>(() => provider.GetOriginal(in cell, out _));
        provider.Get(in cell, out _);
        provider.GetOriginal(in cell, out original);
        Assert.That(original, Is.EqualTo(write && !reset ? UInt256.One : UInt256.Zero));
    }

    [Test]
    public void Same_address_different_index([Range(-1, 2)] int snapshot)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 2), new UInt256(_values[2], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 3), new UInt256(_values[3], isBigEndian: true));
        provider.Restore(Snapshot.EmptyPosition, snapshot, Snapshot.EmptyPosition);

        provider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue4);
        Assert.That(storageValue4, Is.EqualTo(new UInt256(_values[Math.Min(snapshot + 1, 1)], isBigEndian: true)));
    }

    [Test]
    public void Commit_restore()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 2), new UInt256(_values[2], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 3), new UInt256(_values[3], isBigEndian: true));
        provider.Commit(Frontier.Instance);
        provider.Set(new StorageCell(ctx.Address2, 1), new UInt256(_values[4], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address2, 2), new UInt256(_values[5], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address2, 3), new UInt256(_values[6], isBigEndian: true));
        provider.Commit(Frontier.Instance);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[7], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 2), new UInt256(_values[8], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 3), new UInt256(_values[9], isBigEndian: true));
        provider.Commit(Frontier.Instance);
        provider.Set(new StorageCell(ctx.Address2, 1), new UInt256(_values[10], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address2, 2), new UInt256(_values[11], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address2, 3), new UInt256(_values[12], isBigEndian: true));
        provider.Commit(Frontier.Instance);
        provider.Restore(Snapshot.Empty);

        provider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue5);
        Assert.That(storageValue5, Is.EqualTo(new UInt256(_values[7], isBigEndian: true)));
        provider.Get(new StorageCell(ctx.Address1, 2), out UInt256 storageValue6);
        Assert.That(storageValue6, Is.EqualTo(new UInt256(_values[8], isBigEndian: true)));
        provider.Get(new StorageCell(ctx.Address1, 3), out UInt256 storageValue7);
        Assert.That(storageValue7, Is.EqualTo(new UInt256(_values[9], isBigEndian: true)));
        provider.Get(new StorageCell(ctx.Address2, 1), out UInt256 storageValue8);
        Assert.That(storageValue8, Is.EqualTo(new UInt256(_values[10], isBigEndian: true)));
        provider.Get(new StorageCell(ctx.Address2, 2), out UInt256 storageValue9);
        Assert.That(storageValue9, Is.EqualTo(new UInt256(_values[11], isBigEndian: true)));
        provider.Get(new StorageCell(ctx.Address2, 3), out UInt256 storageValue10);
        Assert.That(storageValue10, Is.EqualTo(new UInt256(_values[12], isBigEndian: true)));
    }

    [Test]
    public void Commit_no_changes()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 2), new UInt256(_values[2], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 3), new UInt256(_values[3], isBigEndian: true));
        provider.Restore(Snapshot.Empty);
        provider.Commit(Frontier.Instance);

        provider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue11);
        Assert.That(storageValue11.IsZero, Is.True);
    }

    [Test]
    public void Commit_no_changes_2()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        Snapshot initial = provider.TakeSnapshot();
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        Snapshot first = provider.TakeSnapshot();
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        Snapshot second = provider.TakeSnapshot();
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[3], isBigEndian: true));
        Snapshot third = provider.TakeSnapshot();
        provider.Restore(third);
        provider.Restore(second);
        provider.Restore(first);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[3], isBigEndian: true));
        provider.Restore(initial);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Get(new StorageCell(ctx.Address1, 1), out _);
        provider.Commit(Frontier.Instance);

        provider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue12);
        Assert.That(storageValue12.IsZero, Is.True);
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
            storageProvider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
            storageProvider.Commit(Frontier.Instance);
            storageProvider.Commit(Frontier.Instance);
            storageProvider.CommitTree(0);
            stateRoot = ctx.StateProvider.StateRoot;
        }
        BlockHeader newBase = Build.A.BlockHeader.WithStateRoot(stateRoot).TestObject;

        // block 2
        using (IDisposable _ = storageProvider.BeginScope(newBase))
        {
            storageProvider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
            storageProvider.Commit(Frontier.Instance);
            storageProvider.CommitTree(0);
        }

        using (IDisposable _ = storageProvider.BeginScope(newBase))
        {
            Assert.That(storageProvider.AccountExists(ctx.Address1), Is.True);

            storageProvider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue51);
            byte[] valueAfter = storageValue51.ToMinimalBigEndian();

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
                provider.Set(new StorageCell(written[i], 1), new UInt256(_values[i + 1], isBigEndian: true));
            }

            for (int i = 0; i < 64; i++)
            {
                provider.Get(new StorageCell(new Address(Keccak.Compute($"r{i}")), 1), out _);
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
                provider.Get(new StorageCell(written[i], 1), out UInt256 storedValue);
                Assert.That(storedValue, Is.EqualTo(new UInt256(_values[i + 1], isBigEndian: true)),
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
            storageProvider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[i % 2], isBigEndian: true));
        }

        storageProvider.Commit(Frontier.Instance);
        ctx.StateProvider.Commit(Frontier.Instance);

        storageProvider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue52);
        byte[] valueAfter = storageValue52.ToMinimalBigEndian();
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
        provider.GetTransientState(new StorageCell(ctx.Address1, 1), out UInt256 storageValue13);
        Assert.That(storageValue13.IsZero, Is.True);

        // Should be 0 if loading from the same contract but different index
        provider.SetTransientState(new StorageCell(ctx.Address1, 2), new UInt256(_values[1], isBigEndian: true));
        provider.GetTransientState(new StorageCell(ctx.Address1, 1), out UInt256 storageValue14);
        Assert.That(storageValue14.IsZero, Is.True);

        // Should be 0 if loading from the same index but different contract
        provider.GetTransientState(new StorageCell(ctx.Address2, 1), out UInt256 storageValue15);
        Assert.That(storageValue15.IsZero, Is.True);
    }

    /// <summary>
    /// Simple transient storage test
    /// </summary>
    [Test]
    public void Can_tload_after_tstore()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);

        provider.SetTransientState(new StorageCell(ctx.Address1, 2), new UInt256(_values[1], isBigEndian: true));
        provider.GetTransientState(new StorageCell(ctx.Address1, 2), out UInt256 storageValue16);
        Assert.That(storageValue16, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));
    }

    /// <summary>
    /// Transient storage can be updated and restored
    /// </summary>
    /// <param name="snapshot">Snapshot to restore to</param>
    [Test]
    public void Tload_same_address_same_index_different_values_restore([Range(-1, 2)] int snapshot)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        Snapshot[] snapshots = new Snapshot[4];
        snapshots[0] = provider.TakeSnapshot();
        provider.SetTransientState(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        snapshots[1] = provider.TakeSnapshot();
        provider.SetTransientState(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        snapshots[2] = provider.TakeSnapshot();
        provider.SetTransientState(new StorageCell(ctx.Address1, 1), new UInt256(_values[3], isBigEndian: true));
        snapshots[3] = provider.TakeSnapshot();

        Assert.That(snapshot, Is.EqualTo(snapshots[snapshot + 1].StorageSnapshot.TransientStorageSnapshot));
        // Persistent storage is unimpacted by transient storage
        Assert.That(snapshots[snapshot + 1].StorageSnapshot.PersistentStorageSnapshot, Is.EqualTo(-1));

        provider.Restore(snapshots[snapshot + 1]);

        provider.GetTransientState(new StorageCell(ctx.Address1, 1), out UInt256 storageValue17);
        Assert.That(storageValue17, Is.EqualTo(new UInt256(_values[snapshot + 1], isBigEndian: true)));
    }

    /// <summary>
    /// Commit will reset transient state
    /// </summary>
    [Test]
    public void Commit_resets_transient_state()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);

        provider.SetTransientState(new StorageCell(ctx.Address1, 2), new UInt256(_values[1], isBigEndian: true));
        provider.GetTransientState(new StorageCell(ctx.Address1, 2), out UInt256 storageValue18);
        Assert.That(storageValue18, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));

        provider.Commit(Frontier.Instance);
        provider.GetTransientState(new StorageCell(ctx.Address1, 2), out UInt256 storageValue19);
        Assert.That(storageValue19.IsZero, Is.True);
    }

    private static readonly UInt256[] StorageWords = [UInt256.Zero, UInt256.One, UInt256.MaxValue, new(0x0123456789abcdef, 0xfedcba9876543210, 0x1020304050607080, 0x8070605040302010)];

    [Test]
    public void Transient_writes_copy_values_and_skip_unchanged_values([ValueSource(nameof(StorageWords))] UInt256 value, [Values] bool traced)
    {
        using Context ctx = new(useFlat);
        IWorldState provider = BuildStorageProvider(ctx);
        if (traced)
        {
            TracedAccessWorldState decorator = new(provider, parallel: false);
            decorator.SetGeneratingBlockAccessList(new());
            provider = decorator;
        }
        StorageCell cell = new(ctx.Address1, 1);
        UInt256 expected = value;
        provider.SetTransientState(cell, in value);
        Snapshot snapshot = provider.TakeSnapshot();
        provider.SetTransientState(cell, in value);
        Assert.That(provider.TakeSnapshot(), Is.EqualTo(snapshot));
        value ^= UInt256.One;
        provider.GetTransientState(cell, out UInt256 storageValue20);
        Assert.That(storageValue20, Is.EqualTo(expected));
        provider.Restore(Snapshot.Empty);
        provider.GetTransientState(cell, out UInt256 storageValue21);
        Assert.That(storageValue21.IsZero, Is.True);
    }

    [Test]
    public void Transient_clear_is_revertible([Values] bool destroy, [Values(0UL, 7UL)] ulong initialValue)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);
        StorageCell otherCell = new(ctx.Address2, 1);
        provider.SetTransientState(in cell, UInt256.One);
        provider.SetTransientState(in cell, (UInt256)initialValue);
        provider.SetTransientState(in otherCell, (UInt256)9);
        Snapshot snapshot = provider.TakeSnapshot();

        if (destroy) provider.MarkStorageDestroyed(ctx.Address1);
        else provider.ClearStorage(ctx.Address1);

        provider.GetTransientState(in cell, out UInt256 cleared);
        provider.GetTransientState(in otherCell, out UInt256 other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(UInt256.Zero));
            Assert.That(other, Is.EqualTo((UInt256)9));
            Assert.That(provider.TakeSnapshot().StorageSnapshot.TransientStorageSnapshot,
                Is.EqualTo(snapshot.StorageSnapshot.TransientStorageSnapshot + (initialValue == 0 ? 0 : 1)));
        }

        provider.Restore(snapshot);
        provider.GetTransientState(in cell, out UInt256 restored);
        Assert.That(restored, Is.EqualTo((UInt256)initialValue));
    }

    [Test]
    public void Transient_write_invokes_decorator_override()
    {
        using Context ctx = new(useFlat);
        CountingWorldStateDecorator decorator = new(BuildStorageProvider(ctx));
        IWorldState provider = decorator;
        StorageCell cell = new(ctx.Address1, 1);
        provider.SetTransientState(cell, UInt256.One);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decorator.TransientWrites, Is.EqualTo(1));
            provider.GetTransientState(cell, out UInt256 storageValue22);
            Assert.That(storageValue22, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));
        }
    }

    [Test]
    public void Writes_coalesce_between_snapshots([Values] bool revertChild, [Values] bool transient)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);
        void Set(byte[] value)
        {
            if (transient) provider.SetTransientState(cell, new UInt256(value, isBigEndian: true));
            else provider.Set(cell, new UInt256(value, isBigEndian: true));
        }
        byte[] Get()
        {
            UInt256 value;
            if (transient) provider.GetTransientState(in cell, out value);
            else provider.Get(in cell, out value);
            return value.ToMinimalBigEndian();
        }
        int Position(Snapshot snapshot) => transient ? snapshot.StorageSnapshot.TransientStorageSnapshot : snapshot.StorageSnapshot.PersistentStorageSnapshot;
        Snapshot initial = provider.TakeSnapshot();
        for (int i = 1; i <= 4; i++) Set(_values[i]);
        Snapshot parent = provider.TakeSnapshot();
        Assert.That(Position(parent), Is.EqualTo(Position(initial) + 1));

        for (int i = 5; i <= 8; i++) Set(_values[i]);
        Snapshot child = provider.TakeSnapshot();
        Assert.That(Position(child), Is.EqualTo(Position(parent) + 1));
        if (revertChild)
        {
            provider.Restore(parent);
            Assert.That(Get(), Is.EqualTo(_values[4]));
        }

        for (int i = 9; i <= 12; i++) Set(_values[i]);
        Assert.That(Get(), Is.EqualTo(_values[12]));
        provider.Restore(parent);
        Assert.That(Get(), Is.EqualTo(_values[4]));
        provider.Restore(initial);
        Assert.That(Get().IsZero(), Is.True);
    }

    [Test]
    public void Journal_matches_snapshot_model([Values] bool transient)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell[] cells = [new(ctx.Address1, 1), new(ctx.Address1, 2), new(ctx.Address2, 1)];
        UInt256[] values = new UInt256[cells.Length];
        List<(Snapshot Snapshot, UInt256[] Values)> snapshots = [(provider.TakeSnapshot(), (UInt256[])values.Clone())];
        Random random = new(1153);
        for (int step = 0; step < 2048; step++)
        {
            switch (random.Next(5))
            {
                case 0:
                    snapshots.Add((provider.TakeSnapshot(), (UInt256[])values.Clone()));
                    break;
                case 1:
                    int index = random.Next(snapshots.Count);
                    provider.Restore(snapshots[index].Snapshot);
                    values = (UInt256[])snapshots[index].Values.Clone();
                    snapshots.RemoveRange(index + 1, snapshots.Count - index - 1);
                    break;
                default:
                    int slot = random.Next(cells.Length);
                    values[slot] = StorageWords[random.Next(StorageWords.Length)];
                    if (transient) provider.SetTransientState(cells[slot], in values[slot]);
                    else provider.Set(cells[slot], in values[slot]);
                    break;
            }

            for (int slot = 0; slot < cells.Length; slot++)
            {
                UInt256 actual;
                if (transient) provider.GetTransientState(in cells[slot], out actual);
                else provider.Get(in cells[slot], out actual);
                Assert.That(actual, Is.EqualTo((UInt256)values[slot]), $"Step {step}, slot {slot}");
            }
        }
    }

    /// <summary>
    /// Reset will reset transient state
    /// </summary>
    [Test]
    public void Reset_resets_transient_state()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);

        provider.SetTransientState(new StorageCell(ctx.Address1, 2), new UInt256(_values[1], isBigEndian: true));
        provider.GetTransientState(new StorageCell(ctx.Address1, 2), out UInt256 storageValue23);
        Assert.That(storageValue23, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));

        provider.Reset();
        provider.GetTransientState(new StorageCell(ctx.Address1, 2), out UInt256 storageValue24);
        Assert.That(storageValue24.IsZero, Is.True);
    }

    /// <summary>
    /// Transient state does not impact persistent state
    /// </summary>
    /// <param name="snapshot">Snapshot to restore to</param>
    [Test]
    public void Transient_state_restores_independent_of_persistent_state([Range(-1, 2)] int snapshot)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        Snapshot[] snapshots = new Snapshot[4];

        // No updates
        snapshots[0] = provider.TakeSnapshot();

        // Only update transient
        provider.SetTransientState(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        snapshots[1] = provider.TakeSnapshot();

        // Update both
        provider.SetTransientState(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[9], isBigEndian: true));
        snapshots[2] = provider.TakeSnapshot();

        // Only update persistent
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[8], isBigEndian: true));
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

        provider.GetTransientState(new StorageCell(ctx.Address1, 1), out UInt256 storageValue25);
        Assert.That(storageValue25, Is.EqualTo(new UInt256(_values[snapshot + 1], isBigEndian: true)));
    }

    /// <summary>
    /// Persistent state does not impact transient state
    /// </summary>
    /// <param name="snapshot">Snapshot to restore to</param>
    [Test]
    public void Persistent_state_restores_independent_of_transient_state([Range(-1, 2)] int snapshot)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        Snapshot[] snapshots = new Snapshot[4];

        // No updates
        snapshots[0] = (provider).TakeSnapshot();

        // Only update persistent
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[1], isBigEndian: true));
        snapshots[1] = (provider).TakeSnapshot();

        // Update both
        provider.Set(new StorageCell(ctx.Address1, 1), new UInt256(_values[2], isBigEndian: true));
        provider.SetTransientState(new StorageCell(ctx.Address1, 1), new UInt256(_values[9], isBigEndian: true));
        snapshots[2] = (provider).TakeSnapshot();

        // Only update transient
        provider.SetTransientState(new StorageCell(ctx.Address1, 1), new UInt256(_values[8], isBigEndian: true));
        snapshots[3] = (provider).TakeSnapshot();

        provider.Restore(snapshots[snapshot + 1]);

        // Since we didn't update persistent on the 3rd snapshot
        if (snapshot == 2)
        {
            snapshot--;
        }

        Assert.That(snapshots, Is.EqualTo(new[] { Snapshot.Empty, new Snapshot(new Snapshot.Storage(0, Snapshot.EmptyPosition), Snapshot.EmptyPosition), new Snapshot(new Snapshot.Storage(1, 0), Snapshot.EmptyPosition), new Snapshot(new Snapshot.Storage(1, 1), Snapshot.EmptyPosition) }));

        provider.Get(new StorageCell(ctx.Address1, 1), out UInt256 storageValue26);
        Assert.That(storageValue26, Is.EqualTo(new UInt256(_values[snapshot + 1], isBigEndian: true)));
    }

    /// <summary>
    /// Reset will reset transient state
    /// </summary>
    [Test]
    public void Selfdestruct_clears_cache()
    {
        PreBlockCaches preBlockCaches = new(TestPreBlockCachesConfig.Small);
        using Context ctx = new(useFlat, preBlockCaches: preBlockCaches);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell accessedStorageCell = new(TestItem.AddressA, 1);
        StorageCell nonAccessedStorageCell = new(TestItem.AddressA, 2);
        preBlockCaches.StorageCache.Set(accessedStorageCell, new UInt256([1, 2, 3], isBigEndian: true));
        provider.Get(accessedStorageCell, out _);
        provider.Commit(Paris.Instance);
        provider.ClearStorage(TestItem.AddressA);
        provider.Get(accessedStorageCell, out UInt256 storageValue27);
        Assert.That(storageValue27, Is.EqualTo(UInt256.Zero));
        provider.Get(nonAccessedStorageCell, out UInt256 storageValue28);
        Assert.That(storageValue28, Is.EqualTo(UInt256.Zero));
    }

    // A batch that drops what it held stops accepting storage writes, so every clear the write-back issues has to be
    // re-checked. Each case leaves two clears to make, and pins that the second is never reached.
    [TestCase(StorageWriteStop.BeforeTheFirstClear, 0)]
    [TestCase(StorageWriteStop.AtARemovedAccountClear, 1)]
    [TestCase(StorageWriteStop.AtAContractClear, 1)]
    public void Detached_storage_changes_stop_at_the_clear_that_makes_the_batch_reject_storage_writes(
        StorageWriteStop stop, int expectedStorageBatches)
    {
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        BlockHeader baseBlock;
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            foreach (Address address in (Address[])[TestItem.AddressA, TestItem.AddressB, TestItem.AddressC])
            {
                provider.CreateAccount(address, 1);
                provider.Set(new StorageCell(address, 1), new UInt256(_values[1], isBigEndian: true));
            }

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            if (stop == StorageWriteStop.AtARemovedAccountClear)
            {
                foreach (Address address in (Address[])[TestItem.AddressB, TestItem.AddressC])
                {
                    // Execution reads an account before removing it, and only a removal that saw storage on the
                    // account it read counts as taking that storage with it.
                    provider.GetNonce(address);
                    provider.DeleteAccount(address);
                }
            }
            else
            {
                provider.ClearStorage(TestItem.AddressA);
                provider.ClearStorage(TestItem.AddressB);
                // Would follow the clears, so it also pins that the slot writes are not reached.
                provider.Set(new StorageCell(TestItem.AddressA, 2), new UInt256(_values[2], isBigEndian: true));
            }

            provider.Commit(Frontier.Instance);
            using IWorldStateScopeProvider.IBlockChangeSnapshot snapshot = provider._persistentStorageProvider.DetachBlockChanges();

            IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = Substitute.For<IWorldStateScopeProvider.IWorldStateWriteBatch>();
            bool accepts = stop != StorageWriteStop.BeforeTheFirstClear;
            RejectingStorageWriteBatch storageBatch = new(() => accepts = false);
            writeBatch.AcceptsStorageWrites.Returns(_ => accepts);
            writeBatch.CreateStorageWriteBatch(Arg.Any<Address>(), Arg.Any<int>()).Returns(storageBatch);

            snapshot.WriteTo(writeBatch);

            writeBatch.Received(expectedStorageBatches).CreateStorageWriteBatch(Arg.Any<Address>(), Arg.Any<int>());
            Assert.That(storageBatch.ClearCount, Is.EqualTo(expectedStorageBatches));
        }
    }

    private sealed class RejectingStorageWriteBatch(Action onClear) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public int ClearCount { get; private set; }
        public void Set(in UInt256 index, in UInt256 value) => Assert.Fail("Storage writes must stop after the batch rejects them.");
        public void Clear()
        {
            ClearCount++;
            onClear();
        }
        public void Dispose() { }
    }

    public enum StorageWriteStop
    {
        BeforeTheFirstClear,
        AtARemovedAccountClear,
        AtAContractClear,
    }

    [Test]
    public void Rolling_back_storage_clear_preserves_committed_storage(
        [Values] StorageClearRollback rollback, [Values] bool journaled)
    {
        using Context ctx = new(useFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell previouslyRead = new(TestItem.AddressA, 1);
        StorageCell readAfterClear = new(TestItem.AddressA, 2);

        BlockHeader baseBlock;
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            provider.CreateAccount(TestItem.AddressA, 100);
            provider.Set(previouslyRead, new UInt256(_values[7], isBigEndian: true));
            provider.Set(readAfterClear, new UInt256(_values[8], isBigEndian: true));
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            provider.Get(previouslyRead, out UInt256 storageValue29);
            Assert.That(storageValue29, Is.EqualTo(new UInt256(_values[7], isBigEndian: true)));
            if (journaled) provider.Set(in previouslyRead, in storageValue29);
            Snapshot snapshot = provider.TakeSnapshot();

            provider.ClearStorage(TestItem.AddressA);
            provider.Get(previouslyRead, out UInt256 storageValue30);
            Assert.That(storageValue30, Is.EqualTo(UInt256.Zero));
            provider.Get(readAfterClear, out UInt256 storageValue31);
            Assert.That(storageValue31, Is.EqualTo(UInt256.Zero));
            provider.GetOriginal(in readAfterClear, out UInt256 originalAfterClear);
            Assert.That(originalAfterClear, Is.EqualTo(UInt256.Zero));

            if (rollback == StorageClearRollback.ResetKeepingBlockChanges)
            {
                provider.Set(previouslyRead, new UInt256(_values[1], isBigEndian: true));
                provider.ClearStorage(TestItem.AddressA);
            }

            if (rollback == StorageClearRollback.Snapshot)
            {
                provider.Restore(snapshot);
            }
            else
            {
                provider.Reset(resetBlockChanges: false);
            }

            Assert.Throws<InvalidOperationException>(() => provider.GetOriginal(in readAfterClear, out _));

            using (Assert.EnterMultipleScope())
            {
                provider.Get(previouslyRead, out UInt256 storageValue32);
                Assert.That(storageValue32, Is.EqualTo(new UInt256(_values[7], isBigEndian: true)));
                provider.GetOriginal(in previouslyRead, out UInt256 previouslyReadOriginal);
                Assert.That(previouslyReadOriginal, Is.EqualTo(new UInt256(_values[7], isBigEndian: true)));
                provider.Get(readAfterClear, out UInt256 storageValue33);
                Assert.That(storageValue33, Is.EqualTo(new UInt256(_values[8], isBigEndian: true)));
                provider.GetOriginal(in readAfterClear, out UInt256 readAfterClearOriginal);
                Assert.That(readAfterClearOriginal, Is.EqualTo(new UInt256(_values[8], isBigEndian: true)));
            }

            provider.Commit(Frontier.Instance);
            provider.CommitTree(baseBlock.Number + 1);
            Assert.That(provider.StateRoot, Is.EqualTo(baseBlock.StateRoot));
        }
    }

    [Test]
    public void Clearing_unaccessed_empty_storage_is_a_noop([Values] bool accountExists)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        if (accountExists)
        {
            provider.CreateAccount(TestItem.AddressA, 1);
        }

        Snapshot before = provider.TakeSnapshot();
        provider.ClearStorage(TestItem.AddressA);
        Snapshot after = provider.TakeSnapshot();

        Assert.That(after.StorageSnapshot.PersistentStorageSnapshot,
            Is.EqualTo(before.StorageSnapshot.PersistentStorageSnapshot));
    }

    [Test]
    public void Restored_storage_clear_reuses_dictionary()
    {
        const int ReadCount = 64;

        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell existingCell = new(ctx.Address1, 1);

        provider.Set(existingCell, new UInt256(_values[1], isBigEndian: true));
        provider.Commit(Frontier.Instance);
        object blockChange = GetBlockChange(provider, ctx.Address1);
        Snapshot snapshot = provider.TakeSnapshot();

        provider.ClearStorage(ctx.Address1);
        for (int i = 0; i < ReadCount; i++)
        {
            provider.Get(new StorageCell(ctx.Address1, (UInt256)(i + 2)), out _);
        }

        object clearedDictionary = GetDictionary(blockChange);
        int clearedCapacity = GetCapacity(blockChange);

        provider.Restore(snapshot);
        provider.ClearStorage(ctx.Address1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetDictionary(blockChange), Is.SameAs(clearedDictionary));
            Assert.That(GetCapacity(blockChange), Is.EqualTo(clearedCapacity));
        }
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

        provider.Set(cell, new UInt256(_values[7], isBigEndian: true));
        provider.Commit(Frontier.Instance);

        provider.Get(cell, out UInt256 storageValue34);
        Assert.That(storageValue34, Is.EqualTo(new UInt256(_values[7], isBigEndian: true)), "revived contract's write must survive the previous round's destroy mark");
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
            provider.Set(cell, (UInt256)7);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using (provider.BeginScope(baseBlock))
        {
            provider.Get(cell, out UInt256 storageValue35);
            Assert.That(storageValue35, Is.EqualTo((UInt256)7), "precondition: committed value visible");

            provider.MarkStorageDestroyed(TestItem.AddressA);
            provider.Commit(Frontier.Instance);

            provider.Get(cell, out UInt256 storageValue36);
            Assert.That(storageValue36, Is.EqualTo(UInt256.Zero), "committed prior-block storage must read zero after destroy");
        }
    }

    [Test]
    public void Same_block_revival_reads_zero_for_unrewritten_slots()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell rewritten = new(ctx.Address1, 1);
        StorageCell untouched = new(ctx.Address1, 2);

        provider.Set(rewritten, new UInt256(_values[1], isBigEndian: true));
        provider.Set(untouched, new UInt256(_values[2], isBigEndian: true));
        provider.MarkStorageDestroyed(ctx.Address1);
        provider.Commit(Frontier.Instance);

        provider.Set(rewritten, new UInt256(_values[3], isBigEndian: true));
        provider.Commit(Frontier.Instance);

        provider.Get(rewritten, out UInt256 storageValue37);
        Assert.That(storageValue37, Is.EqualTo(new UInt256(_values[3], isBigEndian: true)), "revived contract's rewritten slot must hold the new value");
        provider.Get(untouched, out UInt256 storageValue38);
        Assert.That(storageValue38, Is.EqualTo(UInt256.Zero), "un-rewritten slot of a destroyed contract must read zero, not the pre-destroy write");
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
            provider.Set(cell, (UInt256)7);
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
            provider.Get(cell, out UInt256 storageValue39);
            Assert.That(storageValue39, Is.EqualTo(UInt256.Zero), "destroyed storage must be gone from the persisted store, not only from the in-block marker");
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

        provider.Set(rewritten, new UInt256(_values[1], isBigEndian: true));
        provider.Set(untouched, new UInt256(_values[2], isBigEndian: true));
        provider.ClearStorage(ctx.Address1);
        provider.Set(rewritten, new UInt256(_values[3], isBigEndian: true));
        provider.Commit(Frontier.Instance);

        provider.Get(rewritten, out UInt256 storageValue40);
        Assert.That(storageValue40, Is.EqualTo(new UInt256(_values[3], isBigEndian: true)), "redeploy write after in-round destroy must survive the commit");
        provider.Get(untouched, out UInt256 storageValue41);
        Assert.That(storageValue41, Is.EqualTo(UInt256.Zero), "un-rewritten slot of the destroyed contract must stay zero");
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
            provider.Set(new StorageCell(TestItem.AddressA, 100), (UInt256)1);
            provider.Set(new StorageCell(TestItem.AddressA, 200), (UInt256)2);

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
            provider.Set(new StorageCell(TestItem.AddressA, 101), (UInt256)10);
            provider.Set(new StorageCell(TestItem.AddressA, 200), (UInt256)2);

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
            provider.Set(new StorageCell(TestItem.AddressA, 100), (UInt256)1);
            provider.Set(new StorageCell(TestItem.AddressA, 200), (UInt256)2);

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
            provider.Set(new StorageCell(TestItem.AddressA, 100), (UInt256)1);
            provider.Set(new StorageCell(TestItem.AddressA, 200), (UInt256)2);

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
            provider.Get(new StorageCell(TestItem.AddressA, 100), out UInt256 storageValue42);
            Assert.That(storageValue42, Is.EqualTo(UInt256.Zero));

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
            provider.Set(new StorageCell(TestItem.AddressA, 100), (UInt256)1);
            provider.Set(new StorageCell(TestItem.AddressA, 200), (UInt256)2);
            provider.ClearStorage(TestItem.AddressA);
            provider.DeleteAccount(TestItem.AddressA);

            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);

            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        Assert.That(baseBlock.StateRoot, Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [Test]
    public void Selfdestruct_persist_between_commit()
    {
        PreBlockCaches preBlockCaches = new(TestPreBlockCachesConfig.Small);
        using Context ctx = new(useFlat, preBlockCaches: preBlockCaches);
        StorageCell accessedStorageCell = new(TestItem.AddressA, 1);
        preBlockCaches.StorageCache.Set(accessedStorageCell, new UInt256([1, 2, 3], isBigEndian: true));

        WorldState provider = BuildStorageProvider(ctx);
        provider.Get(accessedStorageCell, out UInt256 storageValue43);
        Assert.That(storageValue43, Is.EqualTo((UInt256)0x010203));
        provider.ClearStorage(TestItem.AddressA);
        provider.Commit(Paris.Instance);
        provider.Get(accessedStorageCell, out UInt256 storageValue44);
        Assert.That(storageValue44, Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void Commit_reports_latest_surviving_write_once([Values] bool clearStorage, [Values] bool restore)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell first = new(ctx.Address1, 100);
        StorageCell second = new(ctx.Address1, 101);
        StorageCell untouched = new(ctx.Address1, 102);
        provider.Set(first, new UInt256(_values[1], isBigEndian: true));
        provider.Set(second, new UInt256(_values[2], isBigEndian: true));
        provider.Set(untouched, new UInt256(_values[3], isBigEndian: true));
        provider.Commit(Frontier.Instance);
        provider.Get(first, out _);
        provider.Get(second, out _);
        provider.Get(untouched, out _);
        provider.Set(first, new UInt256(_values[4], isBigEndian: true));
        provider.Set(second, new UInt256(_values[5], isBigEndian: true));
        Snapshot snapshot = provider.TakeSnapshot();
        if (clearStorage) provider.ClearStorage(ctx.Address1);
        provider.Set(first, new UInt256(_values[6], isBigEndian: true));
        provider.Set(second, new UInt256(_values[7], isBigEndian: true));
        provider.Set(first, new UInt256(_values[8], isBigEndian: true));
        if (restore) provider.Restore(snapshot);
        ReadCollectingStorageTracer tracer = new();

        provider.Commit(Frontier.Instance, tracer);

        byte[] firstValue = _values[restore ? 4 : 8];
        byte[] secondValue = _values[restore ? 5 : 7];
        using (Assert.EnterMultipleScope())
        {
            provider.Get(first, out UInt256 storageValue45);
            Assert.That(storageValue45, Is.EqualTo(new UInt256(firstValue, isBigEndian: true)));
            provider.Get(second, out UInt256 storageValue46);
            Assert.That(storageValue46, Is.EqualTo(new UInt256(secondValue, isBigEndian: true)));
            provider.Get(untouched, out UInt256 storageValue47);
            Assert.That(storageValue47, Is.EqualTo(new UInt256(_values[clearStorage && !restore ? 0 : 3], isBigEndian: true)));
            Assert.That(tracer.Changes, Has.Count.EqualTo(clearStorage && !restore ? 3 : 2));
            Assert.That(tracer.Changes[0].Cell, Is.EqualTo(first), "storage changes follow surviving-head insertion order");
            Assert.That(tracer.Changes[1].Cell, Is.EqualTo(second));
            Assert.That(tracer.Changes.FindAll(change => change.Cell.Equals(first)), Has.Count.EqualTo(1));
            Assert.That(tracer.Changes.Find(change => change.Cell.Equals(first)).Before, Is.EqualTo(_values[1]));
            Assert.That(tracer.Changes.Find(change => change.Cell.Equals(first)).After, Is.EqualTo(firstValue));
            Assert.That(tracer.Changes.Find(change => change.Cell.Equals(second)).Before, Is.EqualTo(_values[2]));
            Assert.That(tracer.Changes.Find(change => change.Cell.Equals(second)).After, Is.EqualTo(secondValue));
        }
    }

    [Test]
    public void Commit_ReadOnlyRound_ReportsStorageReadsToTracer()
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell readCell = new(TestItem.AddressA, 1);

        provider.Get(readCell, out _);

        ReadCollectingStorageTracer tracer = new();
        provider.Commit(Frontier.Instance, tracer);

        Assert.That(tracer.Reads, Does.Contain(readCell));

        // The round's read capture must be cleared by the read-only commit:
        // a subsequent commit without new reads reports nothing.
        ReadCollectingStorageTracer secondRoundTracer = new();
        provider.Commit(Frontier.Instance, secondRoundTracer);

        Assert.That(secondRoundTracer.Reads, Is.Empty);
    }

    [TestCase(RoundBoundary.None)]
    [TestCase(RoundBoundary.ResetKeepingBlockChanges)]
    [TestCase(RoundBoundary.ReadOnlyCommit)]
    [TestCase(RoundBoundary.CommitAfterWrite)]
    public void Original_available_after_repeat_read(RoundBoundary boundary)
    {
        using Context ctx = new(useFlat);
        WorldState provider = BuildStorageProvider(ctx);
        StorageCell cell = new(ctx.Address1, 1);

        provider.Set(cell, new UInt256(_values[1], isBigEndian: true));
        provider.Commit(Frontier.Instance);

        provider.Get(cell, out _);

        switch (boundary)
        {
            case RoundBoundary.ResetKeepingBlockChanges:
                provider.Reset(resetBlockChanges: false);
                break;
            case RoundBoundary.ReadOnlyCommit:
                provider.Commit(Frontier.Instance);
                break;
            case RoundBoundary.CommitAfterWrite:
                // A write in the round is what routes the commit through CommitCore, the clear
                // site that a read-only commit skips.
                provider.Set(new StorageCell(ctx.Address1, 2), new UInt256(_values[2], isBigEndian: true));
                provider.Commit(Frontier.Instance);
                break;
        }

        provider.Get(cell, out _);

        provider.GetOriginal(in cell, out UInt256 originalValue);
        Assert.That(originalValue, Is.EqualTo(new UInt256(_values[1], isBigEndian: true)));
    }

    [Test]
    public void Eip161_pruned_storage_is_unreadable_after_storage_cache_clear()
    {
        using Context ctx = new(useFlat, setInitialState: false);
        IWorldState worldState = ctx.StateProvider;
        StorageCell cell = new(TestItem.AddressA, 1);
        BlockHeader baseBlock;

        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 0);
            worldState.Set(cell, new UInt256((ReadOnlySpan<byte>)[1, 2, 3], isBigEndian: true));
            worldState.Commit(SpuriousDragon.Instance);
            worldState.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).WithNumber(0).TestObject;
        }

        using (worldState.BeginScope(baseBlock))
        {
            Assert.That(worldState.AccountExists(TestItem.AddressA), Is.False);

            Snapshot before = worldState.TakeSnapshot();
            worldState.ClearStorage(TestItem.AddressA);
            Snapshot after = worldState.TakeSnapshot();
            worldState.Get(cell, out UInt256 storageValue48);
            byte[] value = storageValue48.ToMinimalBigEndian();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(after.StorageSnapshot.PersistentStorageSnapshot,
                    Is.EqualTo(before.StorageSnapshot.PersistentStorageSnapshot));
                Assert.That(value, Is.EqualTo(StorageTree.ZeroBytes));
            }
        }
    }

    [Test]
    public void Clear_after_same_block_account_deletion_clears_backing_storage([Values] bool recreateAsBalanceOnly)
    {
        using Context ctx = new(useFlat, setInitialState: false);
        IWorldState worldState = ctx.StateProvider;
        StorageCell cell = new(TestItem.AddressA, 1);
        BlockHeader baseBlock;

        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 0);
            worldState.Set(cell, new UInt256((ReadOnlySpan<byte>)[1, 2, 3], isBigEndian: true));
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).WithNumber(0).TestObject;
        }

        using (worldState.BeginScope(baseBlock))
        {
            worldState.DeleteAccount(TestItem.AddressA);
            worldState.Commit(SpuriousDragon.Instance, commitRoots: false);

            if (recreateAsBalanceOnly)
            {
                worldState.CreateAccount(TestItem.AddressA, 1);
                worldState.Commit(SpuriousDragon.Instance, commitRoots: false);
            }

            worldState.ClearStorage(TestItem.AddressA);

            worldState.Get(cell, out UInt256 storageValue49);
            Assert.That(storageValue49, Is.EqualTo(UInt256.Zero));
        }
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
            worldState.Set(new StorageCell(TestItem.AddressA, 1), new UInt256(Bytes.FromHexString("aaaa"), isBigEndian: true));
            worldState.Commit(SpuriousDragon.Instance);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        using (IDisposable _ = worldState.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject))
        {
            // Set storage to a different value
            worldState.Set(new StorageCell(TestItem.AddressA, 1), new UInt256(Bytes.FromHexString("bbbb"), isBigEndian: true));
            worldState.Commit(SpuriousDragon.Instance);

            // Delete but no clear storage
            worldState.DeleteAccount(TestItem.AddressA);
            worldState.Commit(SpuriousDragon.Instance);

            worldState.CommitTree(1);
            stateRoot = worldState.StateRoot;
        }

        using (IDisposable _ = worldState.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject))
        {
            worldState.Get(new StorageCell(TestItem.AddressA, 1), out UInt256 storageValue50);
            Assert.That(storageValue50.IsZero, Is.True);
        }
    }

    [Test]
    public void Set_empty_value_for_storage_cell_without_read_clears_data([Values(2, 1000)] int numItems)
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
            worldState.Set(new StorageCell(TestItem.AddressA, (UInt256)i), new UInt256(asUInt256.ToBigEndian(), isBigEndian: true));
        }
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(1);

        Hash256 fullHash = worldState.StateRoot;
        Assert.That(fullHash, Is.Not.EqualTo(emptyHash));

        for (int i = 0; i < numItems; i++)
        {
            worldState.Set(new StorageCell(TestItem.AddressA, (UInt256)i), (UInt256)0);
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

        worldState.Set(new StorageCell(TestItem.AddressA, 1), new UInt256(_values[11], isBigEndian: true));
        worldState.Set(new StorageCell(TestItem.AddressA, 2), new UInt256(_values[12], isBigEndian: true));
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(1);

        Hash256 fullHash = worldState.StateRoot;
        Assert.That(fullHash, Is.Not.EqualTo(emptyHash));

        worldState.Get(new StorageCell(TestItem.AddressA, 1), out _);
        worldState.Get(new StorageCell(TestItem.AddressA, 2), out _);
        worldState.Set(new StorageCell(TestItem.AddressA, 1), (UInt256)0);
        worldState.Set(new StorageCell(TestItem.AddressA, 2), (UInt256)0);
        worldState.Commit(Prague.Instance);
        worldState.CommitTree(2);

        Hash256 clearedHash = worldState.StateRoot;

        Assert.That(clearedHash, Is.EqualTo(emptyHash));
    }

    [Test]
    public void Set_pushes_slot_trie_warm_hint_only_from_populator([Values] bool populator)
    {
        PreBlockCaches caches = new(TestPreBlockCachesConfig.Small);
        IWorldStateScopeProvider.IScope mainScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        caches.MainScope = mainScope;

        using Context ctx = new(useFlat, preBlockCaches: populator ? caches : null);
        caches.MainScope = null;
        ctx.StateProvider.Set(new StorageCell(ctx.Address1, 42), new UInt256(_values[1], isBigEndian: true));

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

            public void Set(in UInt256 index, in UInt256 value)
            {
                baseStorageBatch.Set(in index, value);
                writtenData.Slots[new StorageCell(address, index)] = value.ToMinimalBigEndian();
            }

            public void Clear()
            {
                baseStorageBatch.Clear();
                writtenData.SelfDestructed[address] = true;
            }
        }
    }

    public enum RoundBoundary
    {
        None,
        ResetKeepingBlockChanges,
        ReadOnlyCommit,
        CommitAfterWrite,
    }

    public enum StorageClearRollback
    {
        Snapshot,
        ResetKeepingBlockChanges,
    }

    private sealed class ReadCollectingStorageTracer : IWorldStateTracer
    {
        public System.Collections.Generic.List<StorageCell> Reads { get; } = [];
        public System.Collections.Generic.List<(StorageCell Cell, byte[] Before, byte[] After)> Changes { get; } = [];

        public bool IsTracingState => false;
        public bool IsTracingStorage => true;

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) { }
        public void ReportCodeChange(Address address, byte[] before, byte[] after) { }
        public void ReportNonceChange(Address address, UInt256? before, UInt256? after) { }
        public void ReportAccountRead(Address address) { }
        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) { }
        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) => Changes.Add((storageCell, before, after));
        public void ReportStorageRead(in StorageCell storageCell) => Reads.Add(storageCell);
    }
}
