// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>Serves snap account and storage ranges of the response byte limit from a flat state.</summary>
/// <remarks>
/// The state is a persisted base of <see cref="AccountCount"/> accounts and one contract of <see cref="SlotCount"/> slots, under
/// <see cref="InMemoryBlocks"/> blocks held in the trie node buffer as production holds the blocks it has not persisted: each block
/// updates <see cref="AccountsPerBlock"/> accounts and <see cref="SlotsPerBlock"/> slots, and every <see cref="CompactSize"/> blocks
/// are compacted into one snapshot.
/// </remarks>
[MemoryDiagnoser]
public class SnapFlatStateServerBenchmark
{
    private const int AccountCount = 1_000_000;
    private const int SlotCount = 200_000;
    private const int AccountsPerBlock = 300;
    private const int SlotsPerBlock = 300;
    private const int CompactSize = 32;
    private static readonly ValueHash256 MidRange = new("0x8000000000000000000000000000000000000000000000000000000000000000");

    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private INodeRecordBuffer _nodeRecordBuffer = null!;
    private List<FlatSnapshot> _snapshots = null!;
    private List<FlatSnapshot> _compactedBlocks = null!;
    private SnapFlatStateServer _server = null!;
    private Hash256 _rootHash = null!;
    private PathWithAccount[] _storageAccount = null!;

    [Params(0, 128)]
    public int InMemoryBlocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Address[] addresses = new Address[AccountCount];
        Parallel.For(0, AccountCount, i => addresses[i] = DeriveAddress(i));
        Address storageAddress = DeriveAddress(AccountCount);
        Hash256 storageAddressHash = Keccak.Compute(storageAddress.Bytes);

        NodeStorage nodeStorage = new(new MemDb());
        StorageTree storageTree = new(new RawScopedTrieStore(nodeStorage, storageAddressHash), NullLogManager.Instance);
        for (int i = 0; i < SlotCount; i++) storageTree.Set((UInt256)i, [(byte)(i % 255 + 1)]);
        storageTree.Commit();

        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), NullLogManager.Instance);
        for (int i = 0; i < AccountCount; i++) stateTree.Set(addresses[i], new Account((UInt256)i));
        Account storageAccount = new Account(1).WithChangedStorageRoot(storageTree.RootHash);
        stateTree.Set(storageAddress, storageAccount);
        stateTree.Commit();

        StateId stateId = new(0, stateTree.RootHash.ValueHash256);
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        RocksDbPersistence persistence = new(_columnsDb, NullLogManager.Instance, NullTrieNodeLog.Instance);
        new Importer(nodeStorage, persistence, NullLogManager.Instance).Copy(stateId).GetAwaiter().GetResult();

        _nodeRecordBuffer = new NodeRecordBuffer(0, NodeRecordBuffer.DefaultGenerationBytes);
        _snapshots = [];
        _compactedBlocks = [];
        stateId = CommitBlocks(persistence, addresses, storageAddress, stateId);

        _rootHash = stateId.StateRoot.ToCommitment();
        _storageAccount = [new PathWithAccount(storageAddressHash, storageAccount)];
        _server = new SnapFlatStateServer(new InMemoryFlatDbManager(persistence, _nodeRecordBuffer, _snapshots), new SingleStateRootIndex(stateId), NullLogManager.Instance);
    }

    /// <returns>The state of the last block.</returns>
    private StateId CommitBlocks(IPersistence persistence, Address[] addresses, Address storageAddress, StateId stateId)
    {
        FlatDbConfig config = new() { CompactionOffset = 0 };
        ResourcePool resourcePool = new(config);
        // The repository only satisfies the compactor ctor; CompactSnapshotBundle never touches it.
        SnapshotCompactor compactor = new(
            config, new CompactionSchedule(new MemDb(), config, NullLogManager.Instance),
            resourcePool, new SnapshotRepository(null!, null!, NullSnapshotCatalog.Instance, config, null!, NullLogManager.Instance),
            NullLogManager.Instance);
        Random random = new(42);
        List<FlatSnapshot> uncompacted = [];
        for (int block = 1; block <= InMemoryBlocks; block++)
        {
            SnapshotPooledList previous = new(_snapshots.Count + uncompacted.Count);
            foreach (FlatSnapshot snapshot in _snapshots) Lease(previous, snapshot);
            foreach (FlatSnapshot snapshot in uncompacted) Lease(previous, snapshot);
            ReadOnlySnapshotBundle readOnly = new(previous, persistence.CreateReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), _nodeRecordBuffer.PinAll());
            CapturingCommitTarget commitTarget = new();
            using FlatWorldStateScope scope = new(
                currentStateId: stateId,
                snapshotBundle: new SnapshotBundle(readOnly, _nodeRecordBuffer, resourcePool, ResourcePool.Usage.MainBlockProcessing),
                codeDb: new NullCodeDb(),
                commitTarget: commitTarget,
                configuration: config,
                trieCacheWarmer: new NoopTrieWarmer(),
                logManager: NullLogManager.Instance);

            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(AccountsPerBlock + 1))
            {
                foreach (int index in DistinctIndices(random, AccountsPerBlock, AccountCount)) batch.Set(addresses[index], new Account((UInt256)(block + index)));

                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(storageAddress, SlotsPerBlock);
                foreach (int index in DistinctIndices(random, SlotsPerBlock, SlotCount)) storageBatch.Set((UInt256)index, (UInt256)(256 + block + index));
            }

            scope.Commit(blockNumber: (ulong)block);
            resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, commitTarget.LastResource);
            uncompacted.Add(commitTarget.LastSnapshot);
            stateId = new StateId((ulong)block, scope.RootHash);

            if (uncompacted.Count == CompactSize)
            {
                // The block snapshots stay alive, as the repository keeps them until they are persisted.
                using SnapshotPooledList sources = new(CompactSize);
                foreach (FlatSnapshot snapshot in uncompacted) Lease(sources, snapshot);
                _snapshots.Add(compactor.CompactSnapshotBundle(sources));
                _compactedBlocks.AddRange(uncompacted);
                uncompacted.Clear();
            }
        }

        _snapshots.AddRange(uncompacted);
        return stateId;

        static void Lease(SnapshotPooledList list, FlatSnapshot snapshot)
        {
            snapshot.TryAcquire();
            list.Add(snapshot);
        }

        // A write batch takes each key once, as block processing hands it the block's final values.
        static HashSet<int> DistinctIndices(Random random, int count, int maxExclusive)
        {
            HashSet<int> indices = new(count);
            while (indices.Count < count) indices.Add(random.Next(maxExclusive));
            return indices;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (FlatSnapshot snapshot in _snapshots) snapshot.Dispose();
        foreach (FlatSnapshot snapshot in _compactedBlocks) snapshot.Dispose();
        _nodeRecordBuffer.Dispose();
        _columnsDb.Dispose();
    }

    [Benchmark]
    public int AccountRangeFromStart() => ServeAccounts(ValueKeccak.Zero);

    [Benchmark]
    public int AccountRangeFromMiddle() => ServeAccounts(MidRange);

    [Benchmark]
    public int StorageRangeFromMiddle()
    {
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList proofs) =
            _server.GetStorageRanges(_rootHash, _storageAccount, MidRange, null, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        int count = slots[0].Count;
        slots.DisposeRecursive();
        proofs?.Dispose();
        return count;
    }

    private int ServeAccounts(in ValueHash256 startingHash)
    {
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            _server.GetAccountRanges(_rootHash, startingHash, null, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        using (accounts)
        using (proofs)
        {
            return accounts.Count;
        }
    }

    /// <summary>Gathers bundles over the in-memory snapshots and persistence, as <see cref="FlatDbManager"/> does.</summary>
    private sealed class InMemoryFlatDbManager(IPersistence persistence, INodeRecordBuffer nodeRecordBuffer, List<FlatSnapshot> snapshots) : IFlatDbManager
    {
        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) => GatherReadOnlySnapshotBundle(baseBlock, ReaderFlags.None);

        public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock, ReaderFlags readerFlags)
        {
            SnapshotPooledList leased = new(snapshots.Count);
            foreach (FlatSnapshot snapshot in snapshots)
            {
                snapshot.TryAcquire();
                leased.Add(snapshot);
            }

            return new ReadOnlySnapshotBundle(leased, persistence.CreateReader(readerFlags), recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), nodeRecordBuffer.PinAll());
        }

        public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) => throw new NotSupportedException();
        public void FlushCache(CancellationToken cancellationToken) { }
        public bool HasStateForBlock(in StateId stateId) => true;
        public void DropStateNotReachableFrom(in StateId head) { }
        public void AddSnapshot(FlatSnapshot snapshot, TransientResource transientResource) { }
    }

    private sealed class SingleStateRootIndex(StateId stateId) : IFlatStateRootIndex
    {
        public bool HasStateRoot(Hash256 stateRoot) => stateRoot.ValueHash256 == stateId.StateRoot;

        public bool TryGetStateId(Hash256 stateRoot, out StateId id)
        {
            id = stateId;
            return HasStateRoot(stateRoot);
        }
    }
}
