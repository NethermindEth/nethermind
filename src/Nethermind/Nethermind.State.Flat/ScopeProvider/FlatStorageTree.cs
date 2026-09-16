// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer, ISpeculativeTrie
{
    private readonly StorageTree _tree;
    private readonly StorageTree _warmupStorageTree;
    private readonly Address _address;
    private readonly IFlatDbConfig _config;
    private readonly ITrieWarmer _trieCacheWarmer;
    private readonly FlatWorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;
    private readonly ILogger _logger;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx;

    // Speculative root hashing (IFlatDbConfig.SpeculativeStorageRoots): committed slot values are applied to the trie
    // and the changed paths hashed by the scope's bounded workers while later transactions execute. The block thread
    // is the only producer, at most one worker drains a tree at a time, and finalization claims the trie through the
    // same state word, so a worker can never reacquire it after the join.
    // A drain smaller than this only loads and sets; hashing waits for a wider drain or the block end, which hashes all
    // contracts in parallel anyway. Hashing every single write re-hashed hot contracts' paths once per transaction.
    private const int MinDrainToHash = 4;
    private readonly bool _speculate;
    private readonly int _speculationCap;
    private int _speculativeWriteCount;
    private ConcurrentQueue<SpeculativeWrite>? _speculativeQueue;
    private Dictionary<UInt256, UInt256>? _speculativelyApplied;
    private Dictionary<UInt256, UInt256>? _drainBatch;
    private int _speculationState;
    private volatile bool _speculationFailed;
    private Hash256 _speculationBaseRoot;

    // Test seam: runs on the runner right after it releases the trie and before it looks for more work.
    internal Action? OnSpeculationReleased;

    public FlatStorageTree(
        FlatWorldStateScope scope,
        ITrieWarmer trieCacheWarmer,
        SnapshotBundle bundle,
        IFlatDbConfig config,
        ConcurrencyController concurrencyQuota,
        Hash256 storageRoot,
        Address address,
        ILogManager logManager)
    {
        _scope = scope;
        _trieCacheWarmer = trieCacheWarmer;
        _bundle = bundle;
        _address = address;
        _addressHash = address.ToAccountPath.ToHash256();
        _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
        _logger = logManager.GetClassLogger<FlatStorageTree>();

        StorageTrieStoreAdapter storageTrieAdapter = new(bundle, concurrencyQuota, _addressHash);
        StorageTrieStoreWarmerAdapter warmerStorageTrieAdapter = new(bundle, _addressHash);

        _tree = new StorageTree(storageTrieAdapter, storageRoot, logManager);

        // Set the rootref manually. Cut the call to find nodes by about 1/4th.
        _warmupStorageTree = new StorageTree(warmerStorageTrieAdapter, logManager);
        _warmupStorageTree.SetRootHash(storageRoot, false);
        _warmupStorageTree.RootRef = _tree.RootRef;

        _config = config;
        // VerifyWithTrie reads the trie on the block thread during execution, which the runner would race.
        _speculate = config.SpeculativeStorageRoots && !scope.Trieless && !scope.IsReadOnly && !config.VerifyWithTrie;
        _speculationBaseRoot = storageRoot;
        _speculationCap = config.SpeculativeStorageRootContractCap;
    }

    public Hash256 RootHash => _tree.RootHash;

    internal bool IsDisposed => _scope.IsDisposed;

    public void Get(in UInt256 index, out UInt256 value)
    {
        _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();

        // A trie-less (history-backed) scope has no storage trie to verify against — the reader throws on trie-node
        // access, and a historical value verified against the current trie would be wrong anyway.
        if (_config.VerifyWithTrie && !_scope.Trieless)
        {
            _tree.Get(in index, out UInt256 treeValue);
            if (treeValue != value)
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index}. Tree: {treeValue} vs Flat: {value}. Self destruct it {_selfDestructKnownStateIdx}");
            }
        }
    }

    // Reads do not warm the trie: most reads come through the prewarmer, and read-only slots
    // (~30-40% of accesses per @weiihann's analysis) never need their trie path warmed because
    // they don't trigger commit-time tree updates. Warm-up is driven from HintSet on the write
    // path instead.
    public void HintSet(in UInt256 index) => WarmUpSlot(index);

    /// <inheritdoc/>
    /// <remarks>
    /// With <see cref="IFlatDbConfig.SpeculativeStorageRoots"/> the value goes to the trie on a pool thread, which
    /// also loads the path, so the slot needs no separate warm-up. Otherwise only the path is warmed.
    /// </remarks>
    public void HintSet(in UInt256 index, in UInt256 value)
    {
        // Past the cap the contract is wide enough for the parallel block-end bulk update to beat a single worker,
        // which would otherwise still be draining it when the block ends.
        if (!_speculate || _speculativeWriteCount >= _speculationCap || Volatile.Read(ref _speculationState) == SpeculationState.Owned)
        {
            WarmUpSlot(index);
            return;
        }

        _speculativeWriteCount++;

        if (_speculativeQueue is null)
        {
            _speculativeQueue = new();
            _speculativelyApplied = [];
            _scope.RegisterSpeculating(this);
        }

        _speculativeQueue.Enqueue(new SpeculativeWrite(in index, in value));
        if (Interlocked.CompareExchange(ref _speculationState, SpeculationState.Queued, SpeculationState.Idle) == SpeculationState.Idle)
        {
            _scope.EnqueueSpeculation(this);
        }
    }

    /// <summary>Drains the queued writes into the trie once, on one of the scope's speculation workers.</summary>
    public void RunSpeculationOnce()
    {
        // Finalization may have claimed the trie while this tree waited in the scope's work queue.
        if (Interlocked.CompareExchange(ref _speculationState, SpeculationState.Running, SpeculationState.Queued) != SpeculationState.Queued) return;

        ConcurrentQueue<SpeculativeWrite> queue = _speculativeQueue!;
        ApplySpeculativeWrites(queue);
        Interlocked.Exchange(ref _speculationState, SpeculationState.Idle);
        OnSpeculationReleased?.Invoke();

        // Writes enqueued during the drain re-queue the tree behind the other contracts' work. Once finalization
        // has claimed the trie the exchange fails and they are left for the block-end batch.
        if (!queue.IsEmpty && Interlocked.CompareExchange(ref _speculationState, SpeculationState.Queued, SpeculationState.Idle) == SpeculationState.Idle)
        {
            _scope.EnqueueSpeculation(this);
        }
    }

    [SkipLocalsInit]
    private void ApplySpeculativeWrites(ConcurrentQueue<SpeculativeWrite> queue)
    {
        if (_speculationFailed)
        {
            Discard(queue);
            return;
        }

        // Same guard as the warmer: the runner must not be inside the persistence reader when the bundle goes away.
        if (!_bundle.TryLeaseReadOnlyBundle())
        {
            _speculationFailed = true;
            Discard(queue);
            return;
        }

        try
        {
            // Coalesce so a slot rewritten by several transactions costs one trie update, and so a wide drain can take
            // the same prefix-sharing bulk path the block-end batch uses. Parallel work stays off: the scope caps the
            // number of speculation workers, and this is the only hashing budget they get.
            Dictionary<UInt256, UInt256> batch = _drainBatch ??= [];
            batch.Clear();
            while (queue.TryDequeue(out SpeculativeWrite write)) batch[write.Index] = write.Value;
            if (batch.Count == 0) return;

            if (batch.Count > TrieStoreScopeProvider.StorageTreeBulkWriteBatch.MIN_ENTRIES_TO_BATCH)
            {
                using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new(batch.Count);
                ValueHash256 key = default;
                foreach (KeyValuePair<UInt256, UInt256> kv in batch)
                {
                    StorageTree.ComputeKeyWithLookup(kv.Key, ref key);
                    entries.Add(new PatriciaTree.BulkSetEntry(in key, EncodeSlotValue(kv.Value)));
                }

                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entriesRef = entries.ToRef();
                _tree.BulkSet(entriesRef, PatriciaTree.Flags.DoNotParallelize);
            }
            else
            {
                Unsafe.SkipInit(out EvmWord buffer);
                foreach (KeyValuePair<UInt256, UInt256> kv in batch)
                {
                    UInt256 value = kv.Value;
                    _tree.Set(kv.Key, value.IsZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref buffer));
                }
            }

            Dictionary<UInt256, UInt256> applied = _speculativelyApplied!;
            foreach (KeyValuePair<UInt256, UInt256> kv in batch) applied[kv.Key] = kv.Value;

            Db.Metrics.IncrementSpeculativeStorageWrites(batch.Count);
            if (batch.Count >= MinDrainToHash)
            {
                _tree.UpdateRootHash(canBeParallel: false);
                Db.Metrics.IncrementSpeculativeStorageHashPasses();
                _scope.OnSpeculativeStorageRoot(_address, _tree.RootHash);
            }
        }
        catch (Exception e)
        {
            // The trie may hold a half-applied write; the join rewinds it to the base root and the batch redoes the work.
            _speculationFailed = true;
            Discard(queue);
            if (_logger.IsDebug) _logger.Debug($"Speculative storage root update failed for {_address}, falling back to the block-end update: {e}");
        }
        finally
        {
            _bundle.ReleaseReadOnlyBundleLease();
        }

        static void Discard(ConcurrentQueue<SpeculativeWrite> queue)
        {
            while (queue.TryDequeue(out _)) { }
        }
    }

    // The storage trie stores the RLP of the minimal big-endian value; an empty value deletes the leaf.
    private static byte[] EncodeSlotValue(in UInt256 value)
    {
        if (value.IsZero) return [];
        byte[] minimal = value.ToMinimalBigEndian();
        byte[] encoded = new byte[Rlp.LengthOf(minimal)];
        Rlp.Encode(minimal, encoded);
        return encoded;
    }

    /// <summary>Claims the trie from the speculation workers and rewinds it if any of their work failed.</summary>
    /// <remarks>
    /// Ownership moves through the state word: the caller waits for a running worker to release and takes the idle or
    /// queued slot itself, so a worker that still sees queued work cannot reacquire. Writes left in the queue are
    /// dropped; the block-end batch writes every slot the applied set does not already hold. Safe to call more than
    /// once and from the write batch's worker threads.
    /// </remarks>
    internal void JoinSpeculation()
    {
        if (_speculativeQueue is null) return;

        SpinWait spinWait = new();
        long waitStart = Stopwatch.GetTimestamp();
        while (true)
        {
            int state = Volatile.Read(ref _speculationState);
            if (state == SpeculationState.Owned) break;
            if (state == SpeculationState.Running)
            {
                spinWait.SpinOnce();
                continue;
            }

            // Idle or still waiting in the scope's work queue: claim it before a worker gets to it.
            if (Interlocked.CompareExchange(ref _speculationState, SpeculationState.Owned, state) == state) break;
        }

        Db.Metrics.IncrementSpeculativeStorageJoinWaitTicks(Stopwatch.GetElapsedTime(waitStart).Ticks);

        while (_speculativeQueue.TryDequeue(out _)) { }

        if (_speculationFailed)
        {
            _tree.RootHash = _speculationBaseRoot;
            _speculativelyApplied!.Clear();
            _speculationFailed = false;
        }
    }

    private void WarmUpSlot(UInt256 index)
    {
        if (_bundle.ShouldQueuePrewarm(_address, index))
        {
            // ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            if (_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId)
                || _trieCacheWarmer.PushSlotJobMpmc(this, index, _scope.HintSequenceId))
                _scope.IncrementOutstandingWarmups();
        }
    }

    // Called by trie warmer.
    public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
    {
        try
        {
            if (_scope.HintSequenceId != sequenceId || _scope._pausePrewarmer)
            {
                return false;
            }

            if (!_bundle.TryLeaseReadOnlyBundle())
            {
                return false;
            }

            try
            {
                // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
                // this is just to warm up the nodes.
                ValueHash256 key = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(index, ref key);

                _warmupStorageTree.WarmUpPath(key.BytesAsSpan);
                return true;
            }
            finally
            {
                _bundle.ReleaseReadOnlyBundleLease();
            }
        }
        finally
        {
            _scope.DecrementOutstandingWarmups();
        }
    }

    private void Set(in UInt256 slot, in UInt256 value) => _bundle.SetChangedSlot(_address, slot, value);

    // The flat overlay still holds the pre-block value of a slot the block-end batch never wrote.
    private void GetPreBlockSlot(in UInt256 index, out UInt256 value)
    {
        _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();
    }

    internal void ClearStorage()
    {
        // Whatever the runner put in the trie is dropped with the old root; the batch rewrites the block's slots.
        JoinSpeculation();
        _speculativelyApplied?.Clear();
        _speculationBaseRoot = Keccak.EmptyTreeHash;

        _bundle.ClearStorage(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        _tree.RootHash = Keccak.EmptyTreeHash;
    }

    public void CommitTree() => _tree.Commit();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);

        // The batches are created serially before the parallel commit loop, so the join waits inside the batch's
        // first use on a worker instead of stalling every contract behind the slowest runner here.
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(estimatedEntries, _tree, onRootUpdated, _address, commit: true);
        return new StorageTreeBulkWriteBatch(trieBatch, this);
    }

    /// <summary>Whether finalization has claimed the trie from the runner.</summary>
    internal bool SpeculationOwnedByFinalization => Volatile.Read(ref _speculationState) == SpeculationState.Owned;

    private readonly struct SpeculativeWrite(in UInt256 index, in UInt256 value)
    {
        public readonly UInt256 Index = index;
        public readonly UInt256 Value = value;
    }

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch,
        FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private bool _joined;
        private int _skipped;
        private Dictionary<UInt256, UInt256>? _speculativelyApplied;
        // Slots the runner wrote that the batch has not confirmed: the provider skips a slot the block wrote back to
        // its pre-block value, so whatever is left here at dispose has to be put back to that value.
        private HashSet<UInt256>? _unconfirmed;

        private void EnsureOwned()
        {
            if (_joined) return;
            _joined = true;
            storageTree.JoinSpeculation();
            if (storageTree._speculativelyApplied is { Count: > 0 } applied)
            {
                _speculativelyApplied = applied;
                _unconfirmed = [.. applied.Keys];
            }
        }

        public void Set(in UInt256 index, in UInt256 value)
        {
            EnsureOwned();
            if (_speculativelyApplied is not null && _speculativelyApplied.TryGetValue(index, out UInt256 applied))
            {
                _unconfirmed!.Remove(index);
                if (applied == value)
                {
                    _skipped++;
                    storageTree.Set(index, value);
                    return;
                }
            }

            trieBatch.Set(in index, value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            EnsureOwned();
            trieBatch.Clear();
            storageTree.ClearStorage();
            _speculativelyApplied = null;
            _unconfirmed = null;
        }

        public void Dispose()
        {
            EnsureOwned();
            if (_unconfirmed is { Count: > 0 })
            {
                foreach (UInt256 index in _unconfirmed)
                {
                    storageTree.GetPreBlockSlot(in index, out UInt256 original);
                    trieBatch.Set(in index, original);
                }

                Db.Metrics.IncrementSpeculativeStorageRestoredWrites(_unconfirmed.Count);
            }

            if (_skipped > 0) Db.Metrics.IncrementSpeculativeStorageSkippedWrites(_skipped);
            if (_speculativelyApplied is not null) trieBatch.MarkSet();
            trieBatch.Dispose();
        }
    }

    // Trie-less scope: only the flat overlay is written; there is no storage trie to maintain.
    private sealed class FlatOverlayStorageWriteBatch(FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value) => storageTree.Set(index, value);

        public void Clear() => storageTree.ClearStorage();

        public void Dispose() { }
    }
}
