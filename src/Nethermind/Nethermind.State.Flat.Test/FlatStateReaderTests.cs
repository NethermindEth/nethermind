// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatStateReaderTests
{
    private static readonly BlockHeader _header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject;

    private INodeRecordBuffer _buffer = null!;

    [SetUp]
    public void SetUp() => _buffer = FlatTestHelpers.NewTestBuffer();

    [TearDown]
    public void TearDown() => _buffer.Dispose();

    private FlatStateReader CreateReader(IFlatDbManager manager) =>
        new(new MemDb(), manager, NullHistoricalTrieVisitor.Instance, LimboLogs.Instance);

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
    public void RunTreeVisitor_OnHistoricalState_ThrowsMissingTrieNodeExceptionWithoutGatheringABundle()
    {
        FlatStateReader reader = CreateReader(new HistoricalStateFlatDbManager());

        MissingTrieNodeException? exception = Assert.Throws<MissingTrieNodeException>(() => reader.RunTreeVisitor(new TreeDumper(), _header));
        Assert.That(exception!.Message, Does.Contain("historical"));
    }

    [Test]
    public void RunTreeVisitor_WhenStateTurnsHistoricalAfterTheRoutingCheck_ThrowsMissingTrieNodeException()
    {
        FlatStateReader reader = CreateReader(new LateHistoricalFlatDbManager());

        MissingTrieNodeException? exception = Assert.Throws<MissingTrieNodeException>(() => reader.RunTreeVisitor(new TreeDumper(), _header));
        Assert.That(exception!.Message, Does.Contain("historical"));
    }

    [Test]
    public void RunTreeVisitor_LoadsTrieRootsFromBundle([Values] bool fullScan)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithSingleAccountAndSlot(TestItem.AddressA, 1, out Hash256 stateRoot);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader, snapshot: null, persisted: null));

        if (fullScan) reader.RunTreeVisitor(new TreeDumper(), header);
        else reader.RunTreeVisitor(new AccountProofCollector(TestItem.AddressA, [(UInt256)1]), header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.EqualTo(1));
            Assert.That(trieReader.StorageRlpLoads, Is.EqualTo(1));
        }
    }

    [Test]
    public void RunTreeVisitor_ReadsSnapshotsBeforeBasePersistence([Values] bool fullScan, [Values] bool persistedSnapshot)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithSingleAccountAndSlot(TestItem.AddressA, 1, out Hash256 stateRoot);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(header), ResourcePool.Usage.MainBlockProcessing, _buffer);
        trieReader.AddRootsTo(snapshot, _buffer);
        using TempPath arenaPath = TempPath.GetTempDirectory();
        using TempPath blobsPath = TempPath.GetTempDirectory();
        using ArenaManager arena = TestFixtureHelpers.CreateArenaManager(arenaPath.Path);
        using BlobArenaManager blobs = new(blobsPath.Path, 4L * 1024 * 1024);
        using PersistedSnapshot? persisted = persistedSnapshot
            ? TestFixtureHelpers.CreatePersistedSnapshot(arena, blobs, snapshot.From, snapshot.To, PersistedSnapshotBuilderTestExtensions.Build(snapshot, blobs))
            : null;
        trieReader.StateAvailable = false;
        trieReader.StorageAvailable = false;
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader, persistedSnapshot ? null : snapshot, persisted));

        AssertVisit(reader, header, stateRoot, fullScan);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.Zero);
            Assert.That(trieReader.StorageRlpLoads, Is.Zero);
        }
    }

    private static void AssertVisit(FlatStateReader reader, BlockHeader header, Hash256 stateRoot, bool fullScan)
    {
        if (fullScan)
        {
            TreeDumper dumper = new();
            reader.RunTreeVisitor(dumper, header);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(dumper.ToString(), Does.Contain("STATE TREE"));
                Assert.That(dumper.ToString(), Does.Contain("ACCOUNT"));
                Assert.That(dumper.ToString(), Does.Not.Contain("MISSING"));
            }
        }
        else
        {
            AccountProofCollector collector = new(TestItem.AddressA, [(UInt256)1]);
            reader.RunTreeVisitor(collector, header);
            AccountProof proof = collector.BuildResult();
            Assert.That(proof.Proof, Has.Length.EqualTo(1));
            Assert.That(proof.StorageProofs, Has.Length.EqualTo(1));
            Assert.That(proof.StorageProofs[0].Proof, Has.Length.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(proof.Address, Is.EqualTo(TestItem.AddressA));
                Assert.That(proof.Balance, Is.EqualTo(UInt256.One));
                Assert.That(proof.Nonce, Is.Zero);
                Assert.That(Keccak.Compute(proof.Proof[0]), Is.EqualTo(stateRoot));
                Assert.That(Keccak.Compute(proof.StorageProofs[0].Proof[0]), Is.EqualTo(proof.StorageRoot));
                Assert.That(proof.StorageProofs[0].Value!.Value.ToArray(), Is.EqualTo(new byte[] { 1 }));
            }
        }
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

    private class CurrentBundleFlatDbManager(IPersistence.IPersistenceReader persistenceReader, Snapshot? snapshot, PersistedSnapshot? persisted) : IFlatDbManager
    {
        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
        {
            snapshot?.AcquireLease();
            SnapshotPooledList snapshots = snapshot is null ? new SnapshotPooledList(0) : FlatTestHelpers.SnapshotList(snapshot);
            PersistedSnapshotStack persistedSnapshots = PersistedSnapshotStack.Empty(false);
            if (persisted is not null)
            {
                Assert.That(persisted.TryAcquire(), Is.True);
                persistedSnapshots = new(new PersistedSnapshotList(1) { persisted }, recordDetailedMetrics: false);
            }
            return new(snapshots, persistenceReader, false, persistedSnapshots, bufferPin: null);
        }

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public void DropStateNotReachableFrom(in StateId head) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }

    private sealed class CountingTrieReader(Hash256 addressHash, StateTree stateTree, StorageTree storageTree) : IPersistence.IPersistenceReader
    {
        public int StateRlpLoads { get; private set; }
        public int StorageRlpLoads { get; private set; }
        public bool StateAvailable { get; set; } = true;
        public bool StorageAvailable { get; set; } = true;

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

            stateRoot = stateTree.RootHash;
            return new CountingTrieReader(addressHash, stateTree, storageTree);
        }

        public void AddRootsTo(Snapshot snapshot, INodeRecordBuffer buffer)
        {
            snapshot.Content.SetStateNodeRlp(buffer, TreePath.Empty, stateTree.GetNodeByPath([])!);
            snapshot.Content.SetStorageNodeRlp(buffer, addressHash, TreePath.Empty, storageTree.GetNodeByPath([])!);
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            StateRlpLoads++;
            return StateAvailable ? stateTree.GetNodeByPath(path.ToNibble()) : null;
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            StorageRlpLoads++;
            return StorageAvailable && address == addressHash ? storageTree.GetNodeByPath(path.ToNibble()) : null;
        }

        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            byte[]? rlp = TryLoadStateRlp(path, flags);
            if (rlp is null) return false;
            action(rlp, arg);
            return true;
        }

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            byte[]? rlp = TryLoadStorageRlp(address, path, flags);
            if (rlp is null) return false;
            action(rlp, arg);
            return true;
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

    private class HistoricalStateFlatDbManager : IFlatDbManager
    {
        public bool IsHistoricalState(in StateId stateId) => true;

        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
            throw new InvalidOperationException("A historical proof must not gather a snapshot bundle.");

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public void DropStateNotReachableFrom(in StateId head) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }

    private class LateHistoricalFlatDbManager : IFlatDbManager
    {
        public bool IsHistoricalState(in StateId stateId) => false;

        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
            new(new SnapshotPooledList(0), new NoopPersistenceReader(), false, PersistedSnapshotStack.Empty(false), bufferPin: null, isHistorical: true);

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public void DropStateNotReachableFrom(in StateId head) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }
}
