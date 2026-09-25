// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatStateReaderTests
{
    private static readonly BlockHeader _header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject;

    private static FlatStateReader CreateReader(IFlatDbManager manager) =>
        new(new MemDb(), manager, new FlatDbConfig(), NullHistoricalTrieVisitor.Instance, LimboLogs.Instance);

    public static readonly TestCaseData[] UnavailableStateReads =
    [
        new TestCaseData((Action<FlatStateReader>)(reader => reader.TryGetAccount(_header, TestItem.AddressA, out _))) { TestName = "TryGetAccount" },
        new TestCaseData((Action<FlatStateReader>)(reader => reader.GetStorage(_header, TestItem.AddressA, 1, out _))) { TestName = "GetStorage" },
        new TestCaseData((Action<FlatStateReader>)(reader => reader.RunTreeVisitor(new TreeDumper(), _header))) { TestName = "RunTreeVisitor" },
    ];

    [TestCaseSource(nameof(UnavailableStateReads))]
    public void Read_WhenStateUnavailable_ThrowsMissingTrieNodeException(Action<FlatStateReader> read)
    {
        FlatStateReader reader = CreateReader(new ThrowingFlatDbManager());

        Assert.Throws<MissingTrieNodeException>(() => read(reader));
    }

    [Test]
    public void RunTreeVisitor_OnHistoricalBundle_ThrowsMissingTrieNodeException()
    {
        FlatStateReader reader = CreateReader(new HistoricalBundleFlatDbManager());

        MissingTrieNodeException? exception = Assert.Throws<MissingTrieNodeException>(() => reader.RunTreeVisitor(new TreeDumper(), _header));
        Assert.That(exception!.Message, Does.Contain("historical"));
    }

    public static readonly TestCaseData[] RlpCacheVisitorSequences =
    [
        new TestCaseData(false, 1) { TestName = "RunTreeVisitor_ProofTwice_SecondRunServedFromRlpCache" },
        new TestCaseData(true, 2) { TestName = "RunTreeVisitor_FullScanThenProof_FullScanLeavesRlpCacheEmpty" },
    ];

    [TestCaseSource(nameof(RlpCacheVisitorSequences))]
    public void RunTreeVisitor_RlpCache_LoadsExpectedTimes(bool fullScanFirst, int expectedLoadsPerTrie)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithSingleAccountAndSlot(TestItem.AddressA, 1, out Hash256 stateRoot);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader));

        if (fullScanFirst) reader.RunTreeVisitor(new TreeDumper(), header);
        else reader.RunTreeVisitor(new AccountProofCollector(TestItem.AddressA, [(UInt256)1]), header);
        reader.RunTreeVisitor(new AccountProofCollector(TestItem.AddressA, [(UInt256)1]), header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.EqualTo(expectedLoadsPerTrie));
            Assert.That(trieReader.StorageRlpLoads, Is.EqualTo(expectedLoadsPerTrie));
        }
    }

    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void Rejects_negative_trie_node_rlp_cache_capacity(int capacity)
    {
        FlatDbConfig config = new() { TrieNodeRlpCacheCapacity = capacity };

        Assert.That(
            () => new FlatStateReader(null!, null!, config, NullHistoricalTrieVisitor.Instance, LimboLogs.Instance),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    private class ThrowingFlatDbManager : IFlatDbManager
    {
        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
            throw new StateNotRetainedException($"State {baseBlock} no longer exists; concurrently removed.");

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public void DropStateNotReachableFrom(in StateId head) { }
        public bool HasStateForBlock(in StateId stateId) => false;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }

    private class CurrentBundleFlatDbManager(IPersistence.IPersistenceReader persistenceReader) : IFlatDbManager
    {
        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
            new(new SnapshotPooledList(0), persistenceReader, false, PersistedSnapshotStack.Empty(false));

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public void DropStateNotReachableFrom(in StateId head) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }

    private sealed class CountingTrieReader(byte[] stateRootRlp, byte[] storageRootRlp) : IPersistence.IPersistenceReader
    {
        public int StateRlpLoads { get; private set; }
        public int StorageRlpLoads { get; private set; }

        public static CountingTrieReader WithSingleAccountAndSlot(Address address, UInt256 slot, out Hash256 stateRoot)
        {
            MemDb trieDb = new();
            Hash256 addressHash = Keccak.Compute(address.Bytes);
            StorageTree storageTree = new(new RawScopedTrieStore(trieDb, addressHash), LimboLogs.Instance);
            storageTree.Set(slot, [0x01]);
            storageTree.Commit();

            StateTree stateTree = new(new RawScopedTrieStore(trieDb), LimboLogs.Instance);
            stateTree.Set(address, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
            stateTree.Commit();

            NodeStorage nodeStorage = new(trieDb);
            stateRoot = stateTree.RootHash;
            return new CountingTrieReader(
                nodeStorage.Get(null, TreePath.Empty, stateTree.RootHash)!,
                nodeStorage.Get(addressHash, TreePath.Empty, storageTree.RootHash)!);
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            StateRlpLoads++;
            return path.Length == 0 ? stateRootRlp : null;
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            StorageRlpLoads++;
            return path.Length == 0 ? storageRootRlp : null;
        }

        public Account? GetAccount(Address address) => null;
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => false;
        public StateId CurrentState => new(5, Keccak.EmptyTreeHash);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
        public void Dispose() { }
    }

    private class HistoricalBundleFlatDbManager : IFlatDbManager
    {
        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
            new(new SnapshotPooledList(0), new NoopPersistenceReader(), false, PersistedSnapshotStack.Empty(false), isHistorical: true);

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public void DropStateNotReachableFrom(in StateId head) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }
}
