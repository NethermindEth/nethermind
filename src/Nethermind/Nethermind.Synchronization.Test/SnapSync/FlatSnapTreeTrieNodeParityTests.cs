// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

/// <summary>
/// Holds the flat snap trees, which build a range's trie as node records, to the trie nodes that a trie of
/// <see cref="TrieNode"/>s committed through <see cref="SnapUpperBoundAdapter"/> persists from the same ranges.
/// </summary>
public class FlatSnapTreeTrieNodeParityTests
{
    private const int KeyCount = 1000;
    private const long RangeBytes = 4000;

    private static readonly FlatDbColumns[] TrieNodeColumns = [FlatDbColumns.StateNodes, FlatDbColumns.StateTopNodes, FlatDbColumns.StorageNodes, FlatDbColumns.FallbackNodes];

    [Test]
    public void Ranges_persist_the_trie_nodes_of_a_trie_node_commit([Values] bool storage, [Values] bool shuffled)
    {
        Func<ISnapTrieFactory, (AddRangeResult Result, bool IsRootPersisted)>[] ranges = Ranges(storage);
        if (shuffled) new Random(7).Shuffle(ranges);

        using SnapshotableMemColumnsDb<FlatDbColumns> recordDb = new();
        using SnapshotableMemColumnsDb<FlatDbColumns> trieNodeDb = new();
        FlatSnapTrieFactory recordFactory = new(
            new RocksDbPersistence(recordDb, LimboLogs.Instance, NullTrieNodeLog.Instance),
            Substitute.For<IPersistenceManager>(), recordDb, new MemDb(), new TestSyncConfig(), LimboLogs.Instance);
        TrieNodeFactory trieNodeFactory = new(new RocksDbPersistence(trieNodeDb, LimboLogs.Instance, NullTrieNodeLog.Instance));

        foreach (Func<ISnapTrieFactory, (AddRangeResult Result, bool IsRootPersisted)> addRange in ranges)
        {
            (AddRangeResult Result, bool IsRootPersisted) expected = addRange(trieNodeFactory);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(expected.Result, Is.EqualTo(AddRangeResult.OK), "guards the premise");
                Assert.That(addRange(recordFactory), Is.EqualTo(expected));
            }
        }

        Assert.That(TrieNodes(recordDb), Is.EqualTo(TrieNodes(trieNodeDb)).And.Not.Empty);
    }

    /// <summary>The ranges, of a few kilobytes each, that an honest snap server returns over a trie of keys sharing prefixes of every length, ending with a proof of absence.</summary>
    /// <param name="storage">Whether the ranges are of one account's storage rather than of the state.</param>
    private static Func<ISnapTrieFactory, (AddRangeResult Result, bool IsRootPersisted)>[] Ranges(bool storage)
    {
        ValueHash256[] keys = Keys();
        TestRawTrieStore store = new(new TestMemDb());
        StateTree state = new(store.GetTrieStore(null), LimboLogs.Instance);
        PathWithAccount owner = new(keys[0], Build.An.Account.WithBalance(1).TestObject);
        if (storage)
        {
            StorageTree storageTree = new(store.GetTrieStore(owner.Path.ToCommitment()), LimboLogs.Instance);
            foreach (ValueHash256 key in keys) storageTree.Set(key, Rlp.Encode(key.Bytes[..4]), false);
            storageTree.Commit();
            owner = new(owner.Path, owner.Account!.WithChangedStorageRoot(storageTree.RootHash));
            state.Set(owner.Path.ToCommitment(), owner.Account);
        }
        else
        {
            for (int i = 0; i < keys.Length; i++) state.Set(keys[i].ToCommitment(), Build.An.Account.WithBalance((ulong)i + 1).TestObject);
        }

        state.Commit();
        Hash256 stateRoot = state.RootHash;
        SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
        List<Func<ISnapTrieFactory, (AddRangeResult Result, bool IsRootPersisted)>> ranges = [];
        ValueHash256 origin = ValueKeccak.Zero;
        while (true)
        {
            ValueHash256 start = origin;
            byte[][] proof;
            ValueHash256[] paths;
            if (storage)
            {
                (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) =
                    server.GetStorageRanges(stateRoot, [owner], start, ValueKeccak.MaxValue, RangeBytes, CancellationToken.None);
                PathWithStorageSlot[] entries = slots.Count == 0 ? [] : [.. slots[0]];
                proof = Copy(proofs);
                paths = [.. entries.Select(static entry => entry.Path)];
                slots.DisposeRecursive();
                proofs?.Dispose();
                ranges.Add(factory =>
                {
                    using IByteArrayList rangeProofs = new ByteArrayListAdapter(proof.ToPooledList());
                    (AddRangeResult result, _, _, bool isRootPersisted) = SnapProviderHelper.AddStorageRange(factory, owner, entries, start, ValueKeccak.MaxValue, rangeProofs);
                    return (result, isRootPersisted);
                });
            }
            else
            {
                (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                    server.GetAccountRanges(stateRoot, start, ValueKeccak.MaxValue, RangeBytes, CancellationToken.None);
                PathWithAccount[] entries = [.. accounts];
                proof = Copy(proofs);
                paths = [.. entries.Select(static entry => entry.Path)];
                accounts.Dispose();
                proofs.Dispose();
                ranges.Add(factory =>
                {
                    using IByteArrayList rangeProofs = new ByteArrayListAdapter(proof.ToPooledList());
                    return (SnapProviderHelper.AddAccountRange(factory, 0, stateRoot, start, ValueKeccak.MaxValue, entries, rangeProofs).result, false);
                });
            }

            if (paths.Length == 0) break;

            origin = paths[^1].IncrementPath();
        }

        Assert.That(ranges, Has.Count.GreaterThan(10), "guards the premise: the keys span many ranges");
        return [.. ranges];
    }

    /// <summary>Random keys, every other one sharing a prefix with an earlier one, so the trie holds extensions and embedded nodes at every depth.</summary>
    private static ValueHash256[] Keys()
    {
        Random random = new(42);
        List<ValueHash256> keys = [];
        byte[] key = new byte[ValueHash256.MemorySize];
        while (keys.Count < KeyCount)
        {
            random.NextBytes(key);
            if (keys.Count % 2 == 1) keys[random.Next(keys.Count)].Bytes[..random.Next(1, ValueHash256.MemorySize)].CopyTo(key);
            ValueHash256 candidate = new(key);
            if (!keys.Contains(candidate)) keys.Add(candidate);
        }

        return [.. keys];
    }

    private static byte[][] Copy(IByteArrayList? proofs) => proofs is null ? [] : [.. Enumerable.Range(0, proofs.Count).Select(i => proofs[i].ToArray())];

    private static Dictionary<string, string?> TrieNodes(SnapshotableMemColumnsDb<FlatDbColumns> db) =>
        TrieNodeColumns
            .SelectMany(column => db.GetColumnDb(column).GetAll().Select(node => ($"{column}:{node.Key.ToHexString()}", node.Value?.ToHexString())))
            .ToDictionary();

    /// <summary>Builds snap trees of <see cref="TrieNode"/>s, persisting only their trie nodes, as flat snap sync did before node records.</summary>
    private sealed class TrieNodeFactory(IPersistence persistence) : ISnapTrieFactory
    {
        public ISnapTree<PathWithAccount> CreateStateTree() => Create<PathWithAccount>(null);

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) => Create<PathWithStorageSlot>(accountPath.ToCommitment());

        private TrieNodeTree<TEntry> Create<TEntry>(Hash256? address) where TEntry : ISnapEntry
        {
            IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
            IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            SnapUpperBoundAdapter adapter = new(address is null
                ? new PersistenceTrieStoreAdapter(reader, writeBatch, enableDoubleWriteCheck: false)
                : new PersistenceStorageTrieStoreAdapter(reader, writeBatch, address, enableDoubleWriteCheck: false));
            PatriciaTree tree = address is null ? new StateTree(adapter, LimboLogs.Instance) : new StorageTree(adapter, LimboLogs.Instance);
            return new TrieNodeTree<TEntry>(tree, adapter, reader, writeBatch, address);
        }
    }

    private sealed class TrieNodeTree<TEntry>(PatriciaTree tree, SnapUpperBoundAdapter adapter, IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256? address)
        : ISnapTree<TEntry> where TEntry : ISnapEntry
    {
        private List<(TrieNode, TreePath)>? _proofNodes;

        public Hash256 RootHash => tree.RootHash;

        public void SetRootFromProof(IByteArrayList proofs, IReadOnlyList<SnapProofNode> nodes) =>
            _proofNodes = PatriciaSnapProof.SetRoot(tree, proofs, nodes);

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
        {
            ValueHash256 computedHash = default;
            Span<ValueHash256> result = new(ref computedHash);
            bool found = address is null
                ? reader.TryLoadStateRlp(path, ReadFlags.None, static (rlp, hash) => hash[0] = ValueKeccak.Compute(rlp), result)
                : reader.TryLoadStorageRlp(address, path, ReadFlags.None, static (rlp, hash) => hash[0] = ValueKeccak.Compute(rlp), result);
            return found && computedHash == keccak;
        }

        public void BulkSetAndUpdateRootHash(IReadOnlyList<TEntry> entries) => ISnapTree<TEntry>.DoBulkSetAndUpdateRootHash(tree, entries);

        public bool Commit(ValueHash256 upperBound)
        {
            PatriciaSnapProof.StitchBoundaries(_proofNodes, this);
            adapter.UpperBound = upperBound;
            tree.Commit(skipRoot: address is null, WriteFlags.DisableWAL);
            return _proofNodes is not { Count: > 0 } || _proofNodes[0].Item1.IsPersisted;
        }

        public void Dispose()
        {
            writeBatch.Dispose();
            reader.Dispose();
        }
    }
}
