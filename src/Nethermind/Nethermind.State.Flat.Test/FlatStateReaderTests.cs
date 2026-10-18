// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatStateReaderTests
{
    private static readonly BlockHeader _header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(TestItem.KeccakA).TestObject;

    private TrieNodeLog _log = null!;

    [SetUp]
    public void SetUp() => _log = FlatTestHelpers.NewTestLog();

    [TearDown]
    public void TearDown() => _log.Dispose();

    private FlatStateReader CreateReader(IFlatDbManager manager) =>
        new(new MemDb(), manager, NullHistoricalTrieVisitor.Instance, _log);

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
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader));

        if (fullScan) reader.RunTreeVisitor(new TreeDumper(), header);
        else reader.RunTreeVisitor(new AccountProofCollector(TestItem.AddressA, [(UInt256)1]), header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.EqualTo(1));
            Assert.That(trieReader.StorageRlpLoads, Is.EqualTo(1));
        }
    }

    [Test]
    public void RunTreeVisitor_ReadsLogRootsWithoutPersistence([Values] bool fullScan, [Values] bool includeSnapshot)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithSingleAccountAndSlot(TestItem.AddressA, 1, out Hash256 stateRoot);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(header), ResourcePool.Usage.MainBlockProcessing, _log);
        trieReader.AddRootsTo(snapshot, _log);
        trieReader.StateAvailable = false;
        trieReader.StorageAvailable = false;
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader, includeSnapshot ? snapshot : null));

        AssertVisit(reader, header, stateRoot, fullScan);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.Zero);
            Assert.That(trieReader.StorageRlpLoads, Is.Zero);
        }
    }

    [Test]
    public void RunTreeVisitor_ReadsStateAndStorageFromDifferentTiers([Values] bool storageFromLog, [Values] bool fullScan)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithSingleAccountAndSlot(TestItem.AddressA, 1, out Hash256 stateRoot);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(header), ResourcePool.Usage.MainBlockProcessing, _log);
        trieReader.AddRootsTo(snapshot, _log, state: !storageFromLog, storage: storageFromLog);
        trieReader.StateAvailable = storageFromLog;
        trieReader.StorageAvailable = !storageFromLog;
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader));

        AssertVisit(reader, header, stateRoot, fullScan);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.EqualTo(storageFromLog ? 1 : 0));
            Assert.That(trieReader.StorageRlpLoads, Is.EqualTo(storageFromLog ? 0 : 1));
        }
    }

    [Test]
    public void RunTreeVisitor_LoadsUnlinkedStorageChildFromPersistence([Values] bool fullScan)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithSingleAccountAndSlot(TestItem.AddressA, 1, out Hash256 stateRoot, inlineStorage: true);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(header), ResourcePool.Usage.MainBlockProcessing, _log);
        trieReader.AddRootsTo(snapshot, _log);
        trieReader.StateAvailable = false;
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader));

        AssertVisit(reader, header, stateRoot, fullScan, inlineStorage: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.Zero);
            Assert.That(trieReader.StorageRlpLoads, Is.EqualTo(1), "Only the hashed branch below the logged extension needs persistence; its leaves are inline.");
        }
    }

    [Test]
    public void RunTreeVisitor_ReadsPersistedSnapshotsBeforeBasePersistence([Values] bool fullScan)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithSingleAccountAndSlot(TestItem.AddressA, 1, out Hash256 stateRoot);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        using TrieNodeLog sourceLog = FlatTestHelpers.NewTestLog();
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(header), ResourcePool.Usage.MainBlockProcessing, sourceLog);
        trieReader.AddRootsTo(snapshot, sourceLog);
        using TempPath arenaPath = TempPath.GetTempDirectory();
        using TempPath blobsPath = TempPath.GetTempDirectory();
        using ArenaManager arena = TestFixtureHelpers.CreateArenaManager(arenaPath.Path);
        using BlobArenaManager blobs = new(blobsPath.Path, 4L * 1024 * 1024);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, blobs);
        using PersistedSnapshot persisted = TestFixtureHelpers.CreatePersistedSnapshot(arena, blobs, snapshot.From, snapshot.To, data);
        trieReader.StateAvailable = false;
        trieReader.StorageAvailable = false;
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader, persisted: persisted));

        AssertVisit(reader, header, stateRoot, fullScan);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.Zero);
            Assert.That(trieReader.StorageRlpLoads, Is.Zero);
        }
    }

    private static void AssertVisit(FlatStateReader reader, BlockHeader header, Hash256 stateRoot, bool fullScan, bool inlineStorage = false)
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
            Assert.That(proof.StorageProofs[0].Proof, Has.Length.EqualTo(inlineStorage ? 2 : 1));
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

    private class CurrentBundleFlatDbManager(IPersistence.IPersistenceReader persistenceReader, Snapshot? snapshot = null, PersistedSnapshot? persisted = null) : IFlatDbManager
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
            return new(snapshots, persistenceReader, false, persistedSnapshots);
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

        public static CountingTrieReader WithSingleAccountAndSlot(Address address, UInt256 slot, out Hash256 stateRoot, bool inlineStorage = false)
        {
            MemDb trieDb = new();
            Hash256 addressHash = Keccak.Compute(address.Bytes);
            StorageTree storageTree = new(new RawScopedTrieStore(trieDb, addressHash), LimboLogs.Instance);
            storageTree.Set(slot, [0x01]);
            if (inlineStorage)
            {
                byte[] key = new byte[32];
                slot.ToBigEndian(key);
                byte[] siblingKey = Keccak.Compute(key).Bytes.ToArray();
                byte last = siblingKey[^1];
                for (int i = 1; i < 16; i++)
                {
                    siblingKey[^1] = (byte)(last ^ i);
                    storageTree.Set(siblingKey.AsSpan(), new byte[] { 2 });
                }
            }
            storageTree.Commit();

            StateTree stateTree = new(new RawScopedTrieStore(trieDb), LimboLogs.Instance);
            stateTree.Set(address, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
            stateTree.Commit();

            stateRoot = stateTree.RootHash;
            return new CountingTrieReader(addressHash, stateTree, storageTree);
        }

        public void AddRootsTo(Snapshot snapshot, TrieNodeLog log, bool state = true, bool storage = true)
        {
            if (state)
            {
                ulong pointer = snapshot.Content.SetStateNodeRlp(log, TreePath.Empty, stateTree.GetNodeByPath([])!);
                log.SetRoot(null, stateTree.RootHash, pointer);
            }
            if (storage)
            {
                ulong pointer = snapshot.Content.SetStorageNodeRlp(log, addressHash, TreePath.Empty, storageTree.GetNodeByPath([])!);
                log.SetRoot(addressHash, storageTree.RootHash, pointer);
            }
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
            new(new SnapshotPooledList(0), new NoopPersistenceReader(), false, PersistedSnapshotStack.Empty(false), isHistorical: true);

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public void DropStateNotReachableFrom(in StateId head) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) { }
    }
}
