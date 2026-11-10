// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Collections.Generic;
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
using Nethermind.Trie.NodeBuffer;
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
        new(new MemDb(), manager, NullHistoricalTrieVisitor.Instance);

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
        CountingTrieReader trieReader = CountingTrieReader.WithAccountsAndSlot(out Hash256 stateRoot, TestItem.AddressA);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        CurrentBundleFlatDbManager flatDbManager = new(trieReader, snapshot: null, persisted: null);
        FlatStateReader reader = CreateReader(flatDbManager);

        if (fullScan) reader.RunTreeVisitor(new TreeDumper(), header);
        else reader.RunTreeVisitor(new AccountProofCollector(TestItem.AddressA, [(UInt256)1]), header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trieReader.StateRlpLoads, Is.EqualTo(1));
            Assert.That(trieReader.StorageRlpLoads, Is.EqualTo(1));
            Assert.That(flatDbManager.GatheredReaderFlags, Is.EqualTo(fullScan ? ReaderFlags.FullScan : ReaderFlags.None));
        }
    }

    [Test]
    public void RunTreeVisitor_ReadsSnapshotsBeforeBasePersistence([Values] bool fullScan, [Values] bool persistedSnapshot)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithAccountsAndSlot(out Hash256 stateRoot, TestItem.AddressA);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(header), ResourcePool.Usage.MainBlockProcessing, _buffer);
        trieReader.AddNodesTo(snapshot, TestItem.AddressA, TreePath.Empty);
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

    [Test]
    public void RunTreeVisitor_FollowsBufferRecordsAndLoadsTheRestFromPersistence([Values(1, 4)] int parallelism)
    {
        CountingTrieReader trieReader = CountingTrieReader.WithAccountsAndSlot(out Hash256 stateRoot, TestItem.AddressA, TestItem.AddressC);
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).WithStateRoot(stateRoot).TestObject;
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(header), ResourcePool.Usage.MainBlockProcessing, _buffer);
        // The buffer holds the branch root, A's leaf below it and A's storage root; only persistence holds C's leaf and storage root.
        trieReader.AddNodesTo(snapshot, TestItem.AddressA, TreePath.Empty, FlatTestHelpers.CanonicalPath(Keccak.Compute(TestItem.AddressA.Bytes), 1));
        FlatStateReader reader = CreateReader(new CurrentBundleFlatDbManager(trieReader, snapshot, persisted: null));
        TrieStatsCollector collector = new(new MemDb(), LimboLogs.Instance);
        VisitingStats diagnostics = new();

        reader.RunTreeVisitor(collector, header, new VisitingOptions { MaxDegreeOfParallelism = parallelism }, diagnostics);

        TrieStats expected = trieReader.CollectStats();
        // The root branch of two hashes is 83 bytes and each account leaf 107; each storage trie is one 36-byte slot leaf.
        const long stateSize = 83 + 2 * 107;
        const long storageSize = 2 * 36;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(collector.Stats.StateSize, Is.EqualTo(stateSize));
            Assert.That(collector.Stats.StorageSize, Is.EqualTo(storageSize));
            Assert.That(expected.StateSize, Is.EqualTo(stateSize));
            Assert.That(expected.StorageSize, Is.EqualTo(storageSize));
            Assert.That(collector.Stats.StateBranchCount, Is.EqualTo(1));
            Assert.That(collector.Stats.AccountCount, Is.EqualTo(2));
            Assert.That(collector.Stats.StorageLeafCount, Is.EqualTo(2));
            Assert.That(collector.Stats.MissingNodes, Is.Zero);
            Assert.That(trieReader.StateRlpLoads, Is.EqualTo(1));
            Assert.That(trieReader.StorageRlpLoads, Is.EqualTo(1));
            Assert.That(diagnostics.NodeLookups, Is.EqualTo(5));
            Assert.That(diagnostics.CacheMisses, Is.EqualTo(2));
        }
    }

    [Test]
    public void Record_walk_on_one_thread_does_not_allocate([Values] bool fromPersistence)
    {
        // Sixteen leaves under a branch, held either in the buffer or only by persistence.
        Dictionary<TreePath, byte[]> nodes = [];
        byte[]?[] leaves = new byte[16][];
        for (int i = 0; i < 16; i++) nodes[TreePath.Empty.Append(i)] = leaves[i] = FlatTestHelpers.LeafRlp((byte)i);
        byte[] rootRlp = FlatTestHelpers.BranchRlp(leaves);
        Hash256 rootHash = Keccak.Compute(rootRlp);
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, new StateId(_header), ResourcePool.Usage.MainBlockProcessing, _buffer);
        if (!fromPersistence)
        {
            // The fixture builds the same branch above the leaves.
            foreach ((TreePath path, byte[] rlp) in nodes) snapshot.Content.SetStateNodeRlp(_buffer, path, rlp);
            _buffer.SetRoot(null, rootHash, snapshot.Content.StateRootPointer);
        }

        nodes[TreePath.Empty] = rootRlp;
        Snapshot? stateSnapshot = fromPersistence ? null : snapshot;
        using ReadOnlySnapshotBundle bundle = new CurrentBundleFlatDbManager(new PathRlpReader(nodes), stateSnapshot, persisted: null).GatherReadOnlySnapshotBundle(new StateId(_header));
        NodeCounter counter = new();
        NodeRecordBufferVisitor<EmptyContext> walk = new(bundle, stateSnapshot, counter, new VisitingOptions { MaxDegreeOfParallelism = 1 }, diagnostics: null);
        walk.Accept(rootHash);

        // A per-walk allocation shows in every window. A one-off runtime allocation on the test thread, seen in CI
        // while methods tier up, shows in one window only, so the minimum excludes it.
        const int windows = 5;
        long allocated = long.MaxValue;
        for (int window = 0; window < windows; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            walk.Accept(rootHash);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counter.Nodes, Is.EqualTo((1 + windows) * 17));
            Assert.That(allocated, Is.Zero);
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
#pragma warning disable NUnit2045 // The lengths are prerequisites for indexing the proofs below; keep them fail-fast.
            Assert.That(proof.Proof, Has.Length.EqualTo(1));
            Assert.That(proof.StorageProofs, Has.Length.EqualTo(1));
            Assert.That(proof.StorageProofs[0].Proof, Has.Length.EqualTo(1));
#pragma warning restore NUnit2045
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

    /// <summary>Serves state trie node RLP by path without allocating.</summary>
    private sealed class PathRlpReader(Dictionary<TreePath, byte[]> nodes) : SubstitutablePersistenceReader
    {
        public override byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => nodes.GetValueOrDefault(path);
        public override byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => null;
        public override Account? GetAccount(Address address) => null;
        public override bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => false;
        public override StateId CurrentState => new(5, Keccak.EmptyTreeHash);
        public override byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;
        public override bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => false;
        public override IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public override IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public override bool IsPreimageMode => false;
        public override void Dispose() { }
    }

    private sealed class NodeCounter : INodeRecordVisitor<EmptyContext>
    {
        public int Nodes;
        public bool IsFullDbScan => true;
        public bool ExpectAccounts => false;
        public bool ShouldVisit(in EmptyContext nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in EmptyContext nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in EmptyContext nodeContext, in ValueHash256 nodeHash) => throw new TrieException($"Missing node {nodeHash}");
        public void VisitBranch(in EmptyContext nodeContext, NodeRecordRef node) => Nodes++;
        public void VisitExtension(in EmptyContext nodeContext, NodeRecordRef node) => Nodes++;
        public void VisitLeaf(in EmptyContext nodeContext, NodeRecordRef node) => Nodes++;
        public void VisitAccount(in EmptyContext nodeContext, NodeRecordRef node, in AccountStruct account) { }
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
        public ReaderFlags GatheredReaderFlags { get; private set; }

        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) => GatherReadOnlySnapshotBundle(baseBlock, ReaderFlags.None);

        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock, ReaderFlags readerFlags)
        {
            GatheredReaderFlags = readerFlags;
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

    private sealed class CountingTrieReader(StateTree stateTree, Dictionary<Hash256, StorageTree> storageTrees) : IPersistence.IPersistenceReader
    {
        public int StateRlpLoads { get; private set; }
        public int StorageRlpLoads { get; private set; }
        public bool StateAvailable { get; set; } = true;
        public bool StorageAvailable { get; set; } = true;

        /// <summary>A reader over a state trie of <paramref name="addresses"/>, each with a balance of 1 and slot 1 set to 1.</summary>
        public static CountingTrieReader WithAccountsAndSlot(out Hash256 stateRoot, params Address[] addresses)
        {
            MemDb trieDb = new();
            StateTree stateTree = new(new RawScopedTrieStore(trieDb), LimboLogs.Instance);
            Dictionary<Hash256, StorageTree> storageTrees = [];
            foreach (Address address in addresses)
            {
                Hash256 addressHash = Keccak.Compute(address.Bytes);
                StorageTree storageTree = new(new RawScopedTrieStore(trieDb, addressHash), LimboLogs.Instance);
                storageTree.Set(1, [0x01]);
                storageTree.Commit();
                storageTrees[addressHash] = storageTree;
                stateTree.Set(address, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
            }
            stateTree.Commit();

            stateRoot = stateTree.RootHash;
            return new CountingTrieReader(stateTree, storageTrees);
        }

        /// <summary>The stats of the tries as <see cref="PatriciaTree.Accept{TNodeContext}"/> hands their nodes to <see cref="TrieStatsCollector"/>.</summary>
        public TrieStats CollectStats()
        {
            TrieStatsCollector collector = new(new MemDb(), LimboLogs.Instance);
            stateTree.Accept(new NodeRecordVisitorAdapter<TrieStatsCollector.Context>(collector), stateTree.RootHash);
            return collector.Stats;
        }

        /// <summary>Records the state nodes at <paramref name="statePaths"/> and the storage root of <paramref name="address"/> in <paramref name="snapshot"/>, indexing both roots as block processing publishes them.</summary>
        public void AddNodesTo(Snapshot snapshot, Address address, params TreePath[] statePaths)
        {
            INodeRecordBuffer buffer = snapshot.BufferPin!.Buffer;
            foreach (TreePath path in statePaths) snapshot.Content.SetStateNodeRlp(buffer, path, stateTree.GetNodeByPath(path.ToNibble())!);
            Hash256 addressHash = Keccak.Compute(address.Bytes);
            snapshot.Content.SetStorageNodeRlp(buffer, addressHash, TreePath.Empty, storageTrees[addressHash].GetNodeByPath([])!);
            buffer.SetRoot(null, stateTree.RootHash, snapshot.Content.StateRootPointer);
            buffer.SetRoot(addressHash, storageTrees[addressHash].RootHash, snapshot.Content.StorageRoots[addressHash]);
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            StateRlpLoads++;
            return StateAvailable ? stateTree.GetNodeByPath(path.ToNibble()) : null;
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            StorageRlpLoads++;
            return StorageAvailable && storageTrees.TryGetValue(address, out StorageTree? storageTree) ? storageTree.GetNodeByPath(path.ToNibble()) : null;
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
