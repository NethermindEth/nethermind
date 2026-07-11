// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SparseTrieSnapshotPersistenceTests
{
    private const int InitialAccountCount = 32;
    private const int InitialSlotCount = 16;

    [Test]
    public void AccountOnly_DirtyNodesSurviveSnapshotRotationAndColdRead()
    {
        (MemDb trieDb, PatriciaTree reference, Hash256 parentRoot) = CreateAccountTrie();
        Hash256 accountPath = TestItem.Keccaks[0];

        byte[] blockOneAccount = TestItem.GenerateIndexedAccountRlp(1_001);
        Hash256 expectedBlockOneRoot = ApplyAccount(reference, accountPath, blockOneAccount);

        using SparseStateTrie sparseTrie = new();
        using SparseRootComputer firstBlock = new(
            sparseTrie,
            new HalfPathTrieNodeReader(new NodeStorage(trieDb)),
            parentRoot);
        firstBlock.SetAccountChanges(new Dictionary<ValueHash256, LeafUpdate>
        {
            [accountPath.ValueHash256] = LeafUpdate.Changed(blockOneAccount),
        });
        Hash256 sparseBlockOneRoot = firstBlock.ComputeStateRoot();
        Assert.That(sparseBlockOneRoot, Is.EqualTo(expectedBlockOneRoot));

        using SnapshotFixture snapshots = new();
        SparseTrieSnapshotCommitter.CommitAccountTrie(sparseTrie.AccountTrie.Subtrie, snapshots.Bundle);
        Snapshot snapshot = snapshots.Rotate(parentRoot, sparseBlockOneRoot, blockNumber: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.StateNodesCount, Is.GreaterThan(0), "sparse account nodes must be captured");
            Assert.That(snapshot.StorageNodesCount, Is.Zero, "account-only change must not write storage trie nodes");
            Assert.That(snapshot.AccountsCount, Is.Zero, "the fixture persists trie nodes only");
            Assert.That(snapshot.StoragesCount, Is.Zero, "the fixture persists trie nodes only");
        }

        ParentStateTrieNodeReader coldReader = new(snapshots.Bundle);
        byte[] persistedRootRlp = coldReader.LoadStateRlp(TreePath.Empty, sparseBlockOneRoot);
        Assert.That(Keccak.Compute(persistedRootRlp), Is.EqualTo(sparseBlockOneRoot));

        byte[] blockTwoAccount = TestItem.GenerateIndexedAccountRlp(2_001);
        Hash256 expectedBlockTwoRoot = ApplyAccount(reference, accountPath, blockTwoAccount);

        using SparseStateTrie coldTrie = new();
        using SparseRootComputer secondBlock = new(coldTrie, coldReader, sparseBlockOneRoot);
        secondBlock.SetAccountChanges(new Dictionary<ValueHash256, LeafUpdate>
        {
            [accountPath.ValueHash256] = LeafUpdate.Changed(blockTwoAccount),
        });

        Hash256 sparseBlockTwoRoot = secondBlock.ComputeStateRoot();
        Assert.That(sparseBlockTwoRoot, Is.EqualTo(expectedBlockTwoRoot),
            "a fresh sparse trie must be reconstructible from sparse-committed snapshot nodes");
    }

    [Test]
    public void StorageOnly_DirtyNodesSurviveSnapshotRotationAndColdRead()
    {
        MemDb trieDb = new();
        RawTrieStore trieStore = new(trieDb);
        Address address = TestItem.AddressA;
        Hash256 accountPath = Keccak.Compute(address.Bytes);
        UInt256 changedSlot = 3;

        StorageTree storageTree = new(trieStore.GetTrieStore(accountPath), LimboLogs.Instance);
        for (int i = 0; i < InitialSlotCount; i++)
            storageTree.Set((UInt256)i, StorageValue(i + 1));
        storageTree.UpdateRootHash();
        storageTree.Commit();
        Hash256 parentStorageRoot = storageTree.RootHash;

        PatriciaTree stateTree = new(trieStore.GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < InitialAccountCount; i++)
            stateTree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));

        Account parentAccount = AccountWithStorage(nonce: 1, parentStorageRoot);
        stateTree.Set(accountPath.Bytes, EncodeAccount(parentAccount));
        stateTree.UpdateRootHash();
        stateTree.Commit();
        Hash256 parentStateRoot = stateTree.RootHash;

        byte[] blockOneValue = StorageValue(101);
        storageTree.Set(changedSlot, blockOneValue);
        storageTree.UpdateRootHash();
        storageTree.Commit();
        Hash256 expectedBlockOneStorageRoot = storageTree.RootHash;

        Account blockOneAccount = AccountWithStorage(nonce: 1, expectedBlockOneStorageRoot);
        stateTree.Set(accountPath.Bytes, EncodeAccount(blockOneAccount));
        stateTree.UpdateRootHash();
        stateTree.Commit();
        Hash256 expectedBlockOneStateRoot = stateTree.RootHash;

        ValueHash256 slotPath = StoragePath(changedSlot);
        using SparseStateTrie sparseTrie = new();
        using SparseRootComputer firstBlock = new(
            sparseTrie,
            new HalfPathTrieNodeReader(new NodeStorage(trieDb)),
            parentStateRoot);
        firstBlock.AddStorageChanges(accountPath, parentStorageRoot, StorageUpdates(slotPath, blockOneValue));
        Hash256 sparseBlockOneStorageRoot = firstBlock.ComputeStorageRoot(accountPath);
        firstBlock.SetAccountChanges(AccountUpdates(accountPath,
            AccountWithStorage(nonce: 1, sparseBlockOneStorageRoot)));
        Hash256 sparseBlockOneStateRoot = firstBlock.ComputeStateRoot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sparseBlockOneStorageRoot, Is.EqualTo(expectedBlockOneStorageRoot));
            Assert.That(sparseBlockOneStateRoot, Is.EqualTo(expectedBlockOneStateRoot));
        }

        using SnapshotFixture snapshots = new();
        SparseTrieSnapshotCommitter.CommitStorageTrie(
            sparseTrie.StorageTries[accountPath].Subtrie,
            snapshots.Bundle,
            accountPath);
        SparseTrieSnapshotCommitter.CommitAccountTrie(sparseTrie.AccountTrie.Subtrie, snapshots.Bundle);
        Snapshot snapshot = snapshots.Rotate(parentStateRoot, sparseBlockOneStateRoot, blockNumber: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.StateNodesCount, Is.GreaterThan(0), "updated storage root must rewrite account paths");
            Assert.That(snapshot.StorageNodesCount, Is.GreaterThan(0), "sparse storage nodes must be captured");
            Assert.That(snapshot.AccountsCount, Is.Zero, "the fixture persists trie nodes only");
            Assert.That(snapshot.StoragesCount, Is.Zero, "the fixture persists trie nodes only");
        }

        ParentStateTrieNodeReader coldReader = new(snapshots.Bundle);
        byte[] persistedStateRootRlp = coldReader.LoadStateRlp(TreePath.Empty, sparseBlockOneStateRoot);
        byte[] persistedStorageRootRlp = coldReader.LoadStorageRlp(
            accountPath,
            TreePath.Empty,
            sparseBlockOneStorageRoot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Keccak.Compute(persistedStateRootRlp), Is.EqualTo(sparseBlockOneStateRoot));
            Assert.That(Keccak.Compute(persistedStorageRootRlp), Is.EqualTo(sparseBlockOneStorageRoot));
        }

        byte[] blockTwoValue = StorageValue(202);
        storageTree.Set(changedSlot, blockTwoValue);
        storageTree.UpdateRootHash();
        storageTree.Commit();
        Hash256 expectedBlockTwoStorageRoot = storageTree.RootHash;

        Account blockTwoAccount = AccountWithStorage(nonce: 1, expectedBlockTwoStorageRoot);
        stateTree.Set(accountPath.Bytes, EncodeAccount(blockTwoAccount));
        stateTree.UpdateRootHash();
        stateTree.Commit();
        Hash256 expectedBlockTwoStateRoot = stateTree.RootHash;

        using SparseStateTrie coldTrie = new();
        using SparseRootComputer secondBlock = new(coldTrie, coldReader, sparseBlockOneStateRoot);
        secondBlock.AddStorageChanges(
            accountPath,
            sparseBlockOneStorageRoot,
            StorageUpdates(slotPath, blockTwoValue));
        Hash256 sparseBlockTwoStorageRoot = secondBlock.ComputeStorageRoot(accountPath);
        secondBlock.SetAccountChanges(AccountUpdates(accountPath,
            AccountWithStorage(nonce: 1, sparseBlockTwoStorageRoot)));
        Hash256 sparseBlockTwoStateRoot = secondBlock.ComputeStateRoot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sparseBlockTwoStorageRoot, Is.EqualTo(expectedBlockTwoStorageRoot),
                "cold storage proof reads must be satisfied by sparse-committed nodes");
            Assert.That(sparseBlockTwoStateRoot, Is.EqualTo(expectedBlockTwoStateRoot),
                "cold account proof reads must be satisfied by sparse-committed nodes");
        }
    }

    private static (MemDb Db, PatriciaTree Tree, Hash256 Root) CreateAccountTrie()
    {
        MemDb trieDb = new();
        PatriciaTree tree = new(new RawTrieStore(trieDb).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < InitialAccountCount; i++)
            tree.Set(TestItem.Keccaks[i].Bytes, TestItem.GenerateIndexedAccountRlp(i));
        tree.UpdateRootHash();
        tree.Commit();
        return (trieDb, tree, tree.RootHash);
    }

    private static Hash256 ApplyAccount(PatriciaTree tree, Hash256 accountPath, byte[] accountRlp)
    {
        tree.Set(accountPath.Bytes, accountRlp);
        tree.UpdateRootHash();
        tree.Commit();
        return tree.RootHash;
    }

    private static Account AccountWithStorage(ulong nonce, Hash256 storageRoot) =>
        new(nonce, (UInt256)1_000_000, storageRoot, Keccak.OfAnEmptyString);

    private static byte[] EncodeAccount(Account account) => AccountDecoder.Instance.Encode(account).Bytes;

    private static Dictionary<ValueHash256, LeafUpdate> AccountUpdates(Hash256 accountPath, Account account) =>
        new()
        {
            [accountPath.ValueHash256] = LeafUpdate.Changed(EncodeAccount(account)),
        };

    private static Dictionary<ValueHash256, LeafUpdate> StorageUpdates(ValueHash256 slotPath, byte[] value) =>
        new()
        {
            [slotPath] = LeafUpdate.Changed(Rlp.Encode(value).Bytes),
        };

    private static ValueHash256 StoragePath(in UInt256 slot)
    {
        ValueHash256 path = default;
        StorageTree.ComputeKeyWithLookup(slot, ref path);
        return path;
    }

    private static byte[] StorageValue(int seed) =>
        [
            (byte)seed,
            (byte)(seed >> 8),
            0x31,
            0x41,
            0x59,
            0x26,
            0x53,
            0x58,
        ];

    private sealed class SnapshotFixture : IDisposable
    {
        private readonly ResourcePool _pool = new(new FlatDbConfig { CompactSize = 8 });
        private readonly List<TransientResource> _detachedResources = [];

        public SnapshotFixture()
        {
            ReadOnlySnapshotBundle readOnlyBundle = new(
                new SnapshotPooledList(1),
                Substitute.For<IPersistence.IPersistenceReader>(),
                recordDetailedMetrics: false,
                PersistedSnapshotStack.Empty());
            Bundle = new SnapshotBundle(
                readOnlyBundle,
                Substitute.For<ITrieNodeCache>(),
                _pool,
                ResourcePool.Usage.MainBlockProcessing);
        }

        public SnapshotBundle Bundle { get; }

        public Snapshot Rotate(Hash256 fromRoot, Hash256 toRoot, ulong blockNumber)
        {
            StateId from = new(blockNumber - 1, fromRoot.ValueHash256);
            StateId to = new(blockNumber, toRoot.ValueHash256);
            (Snapshot? snapshot, TransientResource? detachedResource) = Bundle.CollectAndApplySnapshot(from, to);
            if (detachedResource is not null)
                _detachedResources.Add(detachedResource);
            return snapshot ?? throw new InvalidOperationException("Snapshot rotation did not return a snapshot");
        }

        public void Dispose()
        {
            Bundle.Dispose();
            foreach (TransientResource detachedResource in _detachedResources)
                detachedResource.Dispose();
        }
    }
}
