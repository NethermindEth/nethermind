// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;

namespace Nethermind.Benchmarks.State;

/// <summary>Applies the snap ranges that an honest server returns to an empty flat database, as flat snap sync does.</summary>
[MemoryDiagnoser]
public class SnapRangeApplyBenchmark
{
    public enum Workload
    {
        AccountRangesOf4KB,
        AccountRangesOf500KB,
        StorageRangesOf4KB,
        StorageRangesOf500KB,

        // Storage tries of one to three slots, each a whole trie in one range without a proof.
        SmallStorageTries,
    }

    private const int KeyCount = 50_000;
    private const int SmallStorageTrieCount = 5_000;

    private Func<ISnapTrieFactory, AddRangeResult>[] _addRanges = null!;

    [ParamsAllValues]
    public Workload Ranges { get; set; }

    [GlobalSetup]
    public void Setup() => _addRanges = Ranges switch
    {
        Workload.AccountRangesOf4KB => ServedRanges(storage: false, 4_000),
        Workload.AccountRangesOf500KB => ServedRanges(storage: false, 500_000),
        Workload.StorageRangesOf4KB => ServedRanges(storage: true, 4_000),
        Workload.StorageRangesOf500KB => ServedRanges(storage: true, 500_000),
        _ => SmallStorageTries(),
    };

    [Benchmark]
    public void ApplyRanges()
    {
        // Without pruning, releasing a snapshot does not scan the whole database, which no real database does.
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new(neverPrune: true);
        FlatSnapTrieFactory factory = new(
            new RocksDbPersistence(db, NullLogManager.Instance, NullTrieNodeLog.Instance),
            Substitute.For<IPersistenceManager>(), db, new MemDb(), new SyncConfig(), NullLogManager.Instance);
        foreach (Func<ISnapTrieFactory, AddRangeResult> addRange in _addRanges)
        {
            AddRangeResult result = addRange(factory);
            if (result != AddRangeResult.OK) throw new InvalidOperationException($"A served range was not applied: {result}.");
        }
    }

    private static ValueHash256[] Keys(int count) => [.. Enumerable.Range(0, count).Select(static i => ValueKeccak.Compute(BitConverter.GetBytes(i)))];

    private static byte[] SlotValue(in ValueHash256 key) => Nethermind.Serialization.Rlp.Rlp.Encode(key.Bytes[..4]);

    private static Func<ISnapTrieFactory, AddRangeResult>[] ServedRanges(bool storage, long rangeBytes)
    {
        ValueHash256[] keys = Keys(KeyCount);
        TestRawTrieStore store = new(new TestMemDb());
        StateTree state = new(store.GetTrieStore(null), NullLogManager.Instance);
        Account ownerAccount = Build.An.Account.WithBalance(1).TestObject;
        if (storage)
        {
            StorageTree storageTree = new(store.GetTrieStore(keys[0].ToCommitment()), NullLogManager.Instance);
            foreach (ValueHash256 key in keys) storageTree.Set(key, SlotValue(key), false);
            storageTree.Commit();
            ownerAccount = ownerAccount.WithChangedStorageRoot(storageTree.RootHash);
            state.Set(keys[0].ToCommitment(), ownerAccount);
        }
        else
        {
            for (int i = 0; i < keys.Length; i++) state.Set(keys[i].ToCommitment(), Build.An.Account.WithBalance((ulong)i + 1).TestObject);
        }

        state.Commit();
        Hash256 stateRoot = state.RootHash;
        PathWithAccount owner = new(keys[0], ownerAccount.ToStruct());
        SnapStateServer server = new(store.AsReadOnly(), NullLogManager.Instance);
        List<Func<ISnapTrieFactory, AddRangeResult>> ranges = [];
        ValueHash256 origin = ValueKeccak.Zero;
        while (true)
        {
            ValueHash256 start = origin;
            byte[][] proof;
            ValueHash256[] paths;
            if (storage)
            {
                (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList proofs) =
                    server.GetStorageRanges(stateRoot, [owner], start, ValueKeccak.MaxValue, rangeBytes, CancellationToken.None);
                PathWithStorageSlot[] entries = slots.Count == 0 ? [] : [.. slots[0]];
                proof = Copy(proofs);
                paths = [.. entries.Select(static entry => entry.Path)];
                slots.DisposeRecursive();
                proofs?.Dispose();
                ranges.Add(factory =>
                {
                    using IByteArrayList rangeProofs = new ByteArrayListAdapter(proof.ToPooledList());
                    return SnapProviderHelper.AddStorageRange(factory, owner, entries, start, ValueKeccak.Zero, ValueKeccak.MaxValue, rangeProofs).result;
                });
            }
            else
            {
                (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                    server.GetAccountRanges(stateRoot, start, ValueKeccak.MaxValue, rangeBytes, CancellationToken.None);
                PathWithAccount[] entries = [.. accounts];
                proof = Copy(proofs);
                paths = [.. entries.Select(static entry => entry.Path)];
                accounts.Dispose();
                proofs.Dispose();
                ranges.Add(factory =>
                {
                    using IByteArrayList rangeProofs = new ByteArrayListAdapter(proof.ToPooledList());
                    return SnapProviderHelper.AddAccountRange(factory, 0, stateRoot, start, ValueKeccak.MaxValue, entries, rangeProofs).result;
                });
            }

            if (paths.Length == 0) break;

            origin = paths[^1].IncrementPath();
        }

        return [.. ranges];
    }

    private static Func<ISnapTrieFactory, AddRangeResult>[] SmallStorageTries()
    {
        ValueHash256[] keys = Keys(SmallStorageTrieCount * 4);
        Func<ISnapTrieFactory, AddRangeResult>[] ranges = new Func<ISnapTrieFactory, AddRangeResult>[SmallStorageTrieCount];
        for (int i = 0; i < SmallStorageTrieCount; i++)
        {
            ValueHash256 address = keys[i * 4];
            PathWithStorageSlot[] slots = [.. keys.Skip(i * 4 + 1).Take(1 + i % 3).Order().Select(static key => new PathWithStorageSlot(key, SlotValue(key)))];
            StorageTree tree = new(new TestRawTrieStore(new TestMemDb()).GetTrieStore(address.ToCommitment()), NullLogManager.Instance);
            foreach (PathWithStorageSlot slot in slots) tree.Set(slot.Path, slot.SlotRlpValue, false);
            tree.UpdateRootHash();
            PathWithAccount owner = new(address, Build.An.Account.WithBalance(1).TestObject.WithChangedStorageRoot(tree.RootHash).ToStruct());
            ranges[i] = factory => SnapProviderHelper.AddStorageRange(factory, owner, slots, null, ValueKeccak.Zero, null).result;
        }

        return ranges;
    }

    private static byte[][] Copy(IByteArrayList proofs) => proofs is null ? [] : [.. Enumerable.Range(0, proofs.Count).Select(i => proofs[i].ToArray())];
}
