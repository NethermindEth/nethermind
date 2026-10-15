// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// The storage-root update of a block that writes a few slots of a large, cold storage trie, after the slots were
/// hinted the way block processing hints them (<see cref="IWorldStateScopeProvider.IStorageTree.HintSet(in UInt256, in UInt256)"/>
/// on each write, then the trie warmer).
/// </summary>
/// <remarks>
/// <para>
/// Every trie node comes from persistence, and each read there is counted and slowed by <see cref="ReadMicros"/> to
/// stand in for a RocksDB block-cache miss, so a node the warm-up did not reach shows up as time in the commit.
/// The <c>Loads/op</c> line printed at cleanup is the number of persistence node reads inside the measured region.
/// </para>
/// <para>
/// The measured region is the block-end write batch, which applies the writes to the storage trie and hashes its
/// root: the part of the commit block processing waits on. Writing the nodes out comes later and is left out.
/// </para>
/// <para>
/// Updating a slot only rewrites the nodes on its path, which the warm-up already loaded. Deleting one can also
/// collapse the branch its leaf hangs from into the one child left, and that child is off the path: with
/// <see cref="WarmupMode.PathOnly"/> it is read during the commit, once per such delete.
/// <see cref="WarmupMode.DeleteAware"/> resolves it in the warm-up instead.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[NoTieredCompilation]
[MinIterationCount(30)]
public class StorageDeleteWarmupBenchmark
{
    public enum WriteKind
    {
        /// <summary>Every changed slot gets a new non-zero value.</summary>
        Update,
        /// <summary>Every changed slot is set to zero, removing its leaf.</summary>
        Delete,
    }

    public enum WarmupMode
    {
        /// <summary>Warm only the path to each written slot, ignoring that a write is a delete.</summary>
        PathOnly,
        /// <summary>Warm the path, and for a delete also the child its branch collapses into.</summary>
        DeleteAware,
    }

    private const ulong BaseBlockNumber = 0;

    [Params(100_000)]
    public int TrieSlots { get; set; }

    // Under PatriciaTree.MinEntriesToParallelizeThreshold, so the trie update runs on one thread, as it does for
    // most contracts in a block; above it the reads spread over threads and hide part of the cost.
    [Params(96)]
    public int ChangedSlots { get; set; }

    [Params(WriteKind.Update, WriteKind.Delete)]
    public WriteKind Writes { get; set; }

    [Params(WarmupMode.PathOnly, WarmupMode.DeleteAware)]
    public WarmupMode Warmup { get; set; }

    /// <summary>Added to every persistence node read; 0 leaves only the in-memory cost.</summary>
    [Params(0, 20)]
    public int ReadMicros { get; set; }

    private FlatDbConfig _config = null!;
    private ResourcePool _resourcePool = null!;
    private RocksDbPersistence _persistence = null!;
    private StateId _baseStateId;
    private Address _address = null!;
    private UInt256[] _slots = null!;

    private FlatWorldStateScope _scope = null!;
    private CountingReader _reader = null!;
    private long _measuredLoads;
    private long _measuredOps;

    [GlobalSetup]
    public void GlobalSetup()
    {
        RequireTieredCompilationDisabled();

        // The idle-thread storage apply would race the measured commit; it is not what this measures.
        _config = new FlatDbConfig { ApplyStorageWritesOnIdleThread = false };
        _resourcePool = new ResourcePool(_config);
        _address = DeriveAddress(7);

        // Build the trie in memory, then move all of it into persistence so every iteration starts cold.
        SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
            new NullTrieNodeCache(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        CapturingCommitTarget commitTarget = new();
        using FlatWorldStateScope scope = new(StateId.PreGenesis, bundle, new NullCodeDb(), commitTarget, _config, new NoopTrieWarmer(), NullLogManager.Instance);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(_address, new Account(balance: 1));
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(_address, estimatedEntries: TrieSlots);
            for (int s = 0; s < TrieSlots; s++) storageBatch.Set(Slot(s), (UInt256)(ulong)(s + 1));
        }

        scope.Commit(blockNumber: BaseBlockNumber);
        _baseStateId = new StateId(BaseBlockNumber, scope.RootHash);

        FlatSnapshot snapshot = commitTarget.LastSnapshot ?? throw new InvalidOperationException("The base commit produced no snapshot");
        _persistence = new RocksDbPersistence(new SnapshotableMemColumnsDb<FlatDbColumns>(), NullLogManager.Instance);
        Persist(_persistence, snapshot);

        // A spread of existing slots, the same every iteration.
        Random random = new(42);
        HashSet<int> picked = [];
        while (picked.Count < ChangedSlots) picked.Add(random.Next(TrieSlots));
        _slots = new UInt256[ChangedSlots];
        int i = 0;
        foreach (int s in picked) _slots[i++] = Slot(s);

        GrowNodeCache();
    }

    /// <summary>
    /// The per-block node cache grows to fit the blocks before it, and a fresh one is a fraction of a running node's.
    /// Warming one large block through the pool first gives the measured blocks a grown cache.
    /// </summary>
    private void GrowNodeCache()
    {
        SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), _persistence.CreateReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
            new NullTrieNodeCache(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        using FlatWorldStateScope scope = new(_baseStateId, bundle, new NullCodeDb(), new CapturingCommitTarget(), _config, new InlineTrieWarmer(WarmupMode.PathOnly), NullLogManager.Instance);
        IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(_address);
        for (int s = 0; s < 4_000; s++) storageTree.HintSet(Slot(s));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _reader = new CountingReader(_persistence.CreateReader(), ReadMicros);
        SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), _reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
            new NullTrieNodeCache(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        InlineTrieWarmer warmer = new(Warmup);
        _scope = new FlatWorldStateScope(_baseStateId, bundle, new NullCodeDb(), new CapturingCommitTarget(), _config, warmer, NullLogManager.Instance);

        // Block processing hints each write as its transaction commits; the warmer then loads the trie paths.
        IWorldStateScopeProvider.IStorageTree storageTree = _scope.CreateStorageTree(_address);
        foreach (UInt256 slot in _slots) storageTree.HintSet(slot, NewValue(slot));

    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _scope.Dispose();
        _scope = null!;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_measuredOps > 0) Console.WriteLine($"// Loads/op {ChangedSlots} {Writes} {Warmup} {ReadMicros}us: {(double)_measuredLoads / _measuredOps:F1}");
    }

    [Benchmark]
    public Hash256? CommitStorageRoot()
    {
        long loadsBefore = _reader.Loads;
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = _scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(_address, estimatedEntries: _slots.Length);
            foreach (UInt256 slot in _slots) storageBatch.Set(slot, NewValue(slot));
        }

        _measuredLoads += _reader.Loads - loadsBefore;
        _measuredOps++;
        return _scope.Get(_address)?.StorageRoot;
    }

    private UInt256 NewValue(in UInt256 slot) => Writes == WriteKind.Delete ? UInt256.Zero : slot + 1_000_000_000;

    // Hashed-looking slots, like mapping entries, so the trie is evenly spread.
    private static UInt256 Slot(int index) => new(Keccak.Compute(BitConverter.GetBytes(index)).Bytes, isBigEndian: true);

    private static void Persist(RocksDbPersistence persistence, FlatSnapshot snapshot)
    {
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(snapshot.From, snapshot.To, WriteFlags.None);
        foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts) batch.SetAccount(kv.Key.Key, kv.Value);
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> kv in snapshot.Storages) batch.SetStorage(kv.Key.Key.Item1, kv.Key.Key.Item2, kv.Value);
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
        {
            if (kv.Value.FullRlp.IsNotNull) batch.SetStateTrieNode(kv.Key.Key, kv.Value.FullRlp.AsSpan());
        }

        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
        {
            if (kv.Value.FullRlp.IsNotNull) batch.SetStorageTrieNode(kv.Key.Key.Item1, kv.Key.Key.Item2, kv.Value.FullRlp.AsSpan());
        }
    }

    /// <summary>Runs each warm-up job inside its push, as a worker that takes it at once would.</summary>
    private sealed class InlineTrieWarmer(WarmupMode mode) : ITrieWarmer
    {
        public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
            => PushSlotJobMpmc(storageTree, index, sequenceId, isDelete);

        public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
        {
            storageTree.WarmUpStorageTrie(index, sequenceId, isDelete && mode == WarmupMode.DeleteAware);
            return true;
        }

        public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
        {
            if (path is null) return false;
            scope.WarmUpStateTrie(path, sequenceId);
            return true;
        }

        public void OnEnterScope() { }

        public void OnExitScope() { }
    }

    /// <summary>Counts persistence trie-node reads and makes each one take <c>readMicros</c>.</summary>
    private sealed class CountingReader(IPersistence.IPersistenceReader inner, int readMicros) : IPersistence.IPersistenceReader
    {
        private long _loads;

        public long Loads => Interlocked.Read(ref _loads);

        private void Read()
        {
            Interlocked.Increment(ref _loads);
            if (readMicros <= 0) return;
            long until = Stopwatch.GetTimestamp() + readMicros * Stopwatch.Frequency / 1_000_000;
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(8);
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            Read();
            return inner.TryLoadStateRlp(path, flags);
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            Read();
            return inner.TryLoadStorageRlp(address, path, flags);
        }

        public Account? GetAccount(Address address) => inner.GetAccount(address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => inner.TryGetSlot(address, slot, ref outValue);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose() => inner.Dispose();
    }
}
