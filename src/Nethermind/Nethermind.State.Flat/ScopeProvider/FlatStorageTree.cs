// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree(
    FlatWorldStateScope scope,
    ITrieWarmer trieCacheWarmer,
    SnapshotBundle bundle,
    IFlatDbConfig config,
    Hash256 storageRoot,
    Address address,
    ILogManager logManager) : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer, ISpeculativeTrie
{
    private readonly Address _address = address;
    private readonly IFlatDbConfig _config = config;
    private readonly ITrieWarmer _trieCacheWarmer = trieCacheWarmer;
    private readonly FlatWorldStateScope _scope = scope;
    private readonly SnapshotBundle _bundle = bundle;
    private readonly ILogManager _logManager = logManager;
    private readonly Hash256 _storageRoot = storageRoot;
    private Hash256? _addressHash;

    private Trees? _trees;

    private const int EarlyIdle = 0;
    private const int EarlyApplying = 1;
    private const int EarlyClaimed = 2;

    // Committed writes waiting for the early apply thread, in commit order.
    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)>? _earlyWrites;
    // Owned by whoever holds _earlyState.
    private StorageTree? _earlyTree;
    private Dictionary<UInt256, UInt256>? _earlyApplied;
    private int _earlyState;
    private int _earlyQueued;
    private readonly int _earlyGeneration = scope.EarlyApplyGeneration;

    // Speculative root hashing (IFlatDbConfig.SpeculativeStorageRoots): committed slot values are applied to the trie
    // and the changed paths hashed by the scope's bounded workers while later transactions execute. The block thread
    // is the only producer, at most one worker drains a tree at a time, and finalization claims the trie through the
    // same state word, so a worker can never reacquire it after the join.
    private readonly bool _speculate = scope.SpeculatesStorageRoots;
    private readonly int _speculationCap = config.SpeculativeStorageRootContractCap;
    private readonly int _minDrainToHash = config.SpeculativeStorageRootMinDrainToHash;
    private readonly int _minDrainToApply = Math.Max(1, config.SpeculativeStorageRootMinDrainToApply);
    private int _speculativeWriteCount;
    private int _pendingSinceQueued;
    private ConcurrentQueue<SpeculativeWrite>? _speculativeQueue;
    private Dictionary<UInt256, UInt256>? _speculativelyApplied;
    private Dictionary<UInt256, UInt256>? _drainBatch;
    private int _speculationState;
    private volatile bool _speculationFailed;
    private Hash256 _speculationBaseRoot = storageRoot;
    // Set once a write batch or a clear takes the trie over. Until then it may hold speculative writes the block never
    // reported, so it reports the pre-block root and the scope commit leaves its nodes out.
    private volatile bool _speculationFinalized;

    // Test seam: runs on the runner right after it releases the trie and before it looks for more work.
    internal Action? OnSpeculationReleased;

    private sealed class Trees(StorageTree tree, StorageTree warmup)
    {
        public readonly StorageTree Tree = tree;
        public readonly StorageTree Warmup = warmup;
        // The root before any write, so a tree built on it copies on write.
        public readonly TrieNode? PreBlockRoot = tree.RootRef;
    }

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

    private Hash256 AddressHash => _addressHash ??= _address.ToAccountPath.ToHash256();

    private Trees GetTrees() => Volatile.Read(ref _trees) ?? CreateTrees();

    private Trees CreateTrees()
    {
        Hash256 addressHash = AddressHash;
        StorageTree tree = new(new StorageTrieStoreAdapter(_bundle, addressHash), _storageRoot, _logManager);

        // Set the rootref manually. Cut the call to find nodes by about 1/4th.
        StorageTree warmup = new(new StorageTrieStoreWarmerAdapter(_bundle, addressHash), _logManager);
        warmup.SetRootHash(_storageRoot, false);
        warmup.RootRef = tree.RootRef;

        Trees created = new(tree, warmup);
        return Interlocked.CompareExchange(ref _trees, created, null) ?? created;
    }

    // As on the serial path, where the trie is untouched until the block-end batch, a trie the workers are still filling
    // reports the pre-block root.
    public Hash256 RootHash => Volatile.Read(ref _speculativeQueue) is not null && !_speculationFinalized
        ? _storageRoot
        : Volatile.Read(ref _trees)?.Tree.RootHash ?? _storageRoot;

    internal bool IsDisposed => _scope.IsDisposed;

    public void Get(in UInt256 index, out UInt256 value)
    {
        _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();

        // A trie-less (history-backed) scope has no storage trie to verify against — the reader throws on trie-node
        // access, and a historical value verified against the current trie would be wrong anyway.
        if (_config.VerifyWithTrie && !_scope.Trieless)
        {
            StorageTree tree = GetTrees().Tree;
            tree.Get(in index, out UInt256 treeValue);
            if (treeValue != value)
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {tree.RootHash}, {index}. Tree: {treeValue} vs Flat: {value}. Self destruct it {_selfDestructKnownStateIdx}");
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
        if (_speculate && _speculativeWriteCount < _speculationCap && Volatile.Read(ref _speculationState) != SpeculationState.Owned)
        {
            Speculate(in index, in value);
            return;
        }

        WarmUpSlot(index);
        if (!_scope.AppliesStorageWritesEarly || Volatile.Read(ref _earlyState) == EarlyClaimed) return;

        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> writes = Volatile.Read(ref _earlyWrites) ?? CreateEarlyWrites();
        writes.Enqueue((index, value));
        // A set flag means a pass that has yet to clear it will see this write.
        if (Volatile.Read(ref _earlyQueued) == 0 && Interlocked.Exchange(ref _earlyQueued, 1) == 0) _scope.EarlyApplier.Enqueue(this);
    }

    private void Speculate(in UInt256 index, in UInt256 value)
    {
        _speculativeWriteCount++;

        if (_speculativeQueue is null)
        {
            _speculativeQueue = new();
            _speculativelyApplied = [];
            _scope.RegisterSpeculating(this);
        }

        _speculativeQueue.Enqueue(new SpeculativeWrite(in index, in value));
        // Hand the tree to a worker only once enough writes have piled up: a drain wide enough to bulk-set pays for
        // the path loads the way the block-end batch does, while a drain of one or two writes loads the same paths
        // for a fraction of the work and a slot written again before the drain is coalesced away for free.
        if (++_pendingSinceQueued < _minDrainToApply) return;

        _pendingSinceQueued = 0;
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

        // Writes enqueued during the drain re-queue the tree behind the other contracts' work, once there are enough
        // of them to be worth a pass. Any that stay queued cost nothing: the block-end batch writes them anyway.
        // Once finalization has claimed the trie the exchange fails and they are left for that batch.
        if (queue.Count >= _minDrainToApply && Interlocked.CompareExchange(ref _speculationState, SpeculationState.Queued, SpeculationState.Idle) == SpeculationState.Idle)
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

            StorageTree tree = GetTrees().Tree;
            Unsafe.SkipInit(out EvmWord buffer);
            if (batch.Count > TrieStoreScopeProvider.StorageTreeBulkWriteBatch.MIN_ENTRIES_TO_BATCH)
            {
                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(batch.Count);
                ValueHash256 key = default;
                foreach (KeyValuePair<UInt256, UInt256> kv in batch)
                {
                    bool isZero = kv.Value.IsZero;
                    StorageTree.ComputeKeyWithLookup(kv.Key, ref key);
                    entries.Add(StorageTree.CreateBulkSetEntry(key, isZero ? StorageTree.ZeroBytes : kv.Value.ToMinimalBigEndian(ref buffer), isZero));
                }

                tree.BulkSet(entries, PatriciaTree.Flags.DoNotParallelize);
            }
            else
            {
                foreach (KeyValuePair<UInt256, UInt256> kv in batch)
                {
                    UInt256 value = kv.Value;
                    tree.Set(kv.Key, value.IsZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref buffer));
                }
            }

            Dictionary<UInt256, UInt256> applied = _speculativelyApplied!;
            foreach (KeyValuePair<UInt256, UInt256> kv in batch) applied[kv.Key] = kv.Value;

            Db.Metrics.IncrementSpeculativeStorageWrites(batch.Count);
            if (batch.Count >= _minDrainToHash)
            {
                tree.UpdateRootHash(canBeParallel: false);
                Db.Metrics.IncrementSpeculativeStorageHashPasses();
                _scope.OnSpeculativeStorageRoot(_address, tree.RootHash);
            }
        }
        catch (Exception e)
        {
            // The trie may hold a half-applied write; the join rewinds it to the base root and the batch redoes the work.
            _speculationFailed = true;
            Discard(queue);
            ILogger logger = _logManager.GetClassLogger<FlatStorageTree>();
            if (logger.IsDebug) logger.Debug($"Speculative storage root update failed for {_address}, falling back to the block-end update: {e}");
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
            GetTrees().Tree.RootHash = _speculationBaseRoot;
            _speculativelyApplied?.Clear();
            _speculationFailed = false;
        }
    }

    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)> CreateEarlyWrites()
    {
        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> created = new();
        return Interlocked.CompareExchange(ref _earlyWrites, created, null) ?? created;
    }

    [SkipLocalsInit]
    internal void ApplyEarlyWrites()
    {
        // Cleared before the queue is read, so a hint landing during the pass queues the tree again.
        Interlocked.Exchange(ref _earlyQueued, 0);
        if (_scope.EarlyApplyClosed || _scope.EarlyApplyGeneration != _earlyGeneration
            || Interlocked.CompareExchange(ref _earlyState, EarlyApplying, EarlyIdle) != EarlyIdle) return;

        bool leased = false;
        try
        {
            ConcurrentQueue<(UInt256 Slot, UInt256 Value)>? writes = Volatile.Read(ref _earlyWrites);
            if (writes is null || writes.IsEmpty || !(leased = _bundle.TryLeaseReadOnlyBundle())) return;

            Dictionary<UInt256, UInt256> applied = _earlyApplied ??= [];
            Dictionary<UInt256, UInt256> latest = new(writes.Count);
            while (writes.TryDequeue(out (UInt256 Slot, UInt256 Value) write)) latest[write.Slot] = write.Value;

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(latest.Count);
            Unsafe.SkipInit(out EvmWord word);
            ValueHash256 key = default;
            foreach ((UInt256 slot, UInt256 value) in latest)
            {
                if (applied.TryGetValue(slot, out UInt256 current) && current == value) continue;

                bool isZero = value.IsZero;
                StorageTree.ComputeKeyWithLookup(slot, ref key);
                entries.Add(StorageTree.CreateBulkSetEntry(key, isZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref word), isZero));
                applied[slot] = value;
            }

            if (entries.Count == 0) return;

            OnEarlyPassDrained?.Invoke();
            StorageTree tree = _earlyTree ??= CreateEarlyTree();
            // No thread-pool work: it would run at normal priority.
            tree.BulkSet(entries, PatriciaTree.Flags.DoNotParallelize);
            tree.UpdateRootHash(canBeParallel: false);
            _scope.CountEarlyApplied(entries.Count);
        }
        catch
        {
            // A partly applied tree must never be adopted.
            _earlyTree = null;
            _earlyApplied = null;
            Volatile.Write(ref _earlyState, EarlyClaimed);
            throw;
        }
        finally
        {
            if (leased) _bundle.ReleaseReadOnlyBundleLease();
            Interlocked.CompareExchange(ref _earlyState, EarlyIdle, EarlyApplying);
        }
    }

    /// <summary>Whether every queued write has been applied to the early tree. For tests.</summary>
    internal bool EarlyWritesDrained => Volatile.Read(ref _earlyWrites) is not { IsEmpty: false } && Volatile.Read(ref _earlyState) != EarlyApplying && Volatile.Read(ref _earlyQueued) == 0;

    /// <summary>Called once a pass has taken its writes. For tests.</summary>
    internal Action? OnEarlyPassDrained;

    private StorageTree CreateEarlyTree()
    {
        // Never the current root: an abandoned pass may still run while the batch writes into the block tree.
        StorageTree tree = new(new StorageTrieStoreWarmerAdapter(_bundle, AddressHash), _logManager);
        tree.SetRootHash(_storageRoot, false);
        tree.RootRef = GetTrees().PreBlockRoot;
        return tree;
    }

    private Dictionary<UInt256, UInt256>? AdoptEarlyTree(StorageTree tree)
    {
        if (Volatile.Read(ref _earlyWrites) is null) return null;

        int previous = Interlocked.Exchange(ref _earlyState, EarlyClaimed);
        if (previous == EarlyApplying)
        {
            // Mid-pass: waiting could take a scheduler tick, so the batch writes every slot instead.
            _scope.CountEarlyAbandoned();
            return null;
        }

        if (previous != EarlyIdle || _earlyTree is not { } earlyTree || _earlyApplied is not { Count: > 0 } applied) return null;

        tree.RootRef = earlyTree.RootRef;
        return applied;
    }

    private void WarmUpSlot(UInt256 index)
    {
        if (_bundle.ShouldQueuePrewarm(_address, index))
        {
            // ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            _scope.IncrementOutstandingWarmups();
            if (!_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId)
                && !_trieCacheWarmer.PushSlotJobMpmc(this, index, _scope.HintSequenceId))
                _scope.DecrementOutstandingWarmups();
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

                GetTrees().Warmup.WarmUpPath(key.BytesAsSpan);
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
        _speculationFinalized = true;
        _speculativelyApplied?.Clear();
        _speculationBaseRoot = Keccak.EmptyTreeHash;

        _bundle.ClearStorage(_address, AddressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        // Trieless scopes too: IWorldState.GetStorageRoot still reads RootHash there.
        GetTrees().Tree.RootHash = Keccak.EmptyTreeHash;
    }

    // Matches PatriciaTree.Commit, which splits the commit, hashing included, across threads above 4 writes.
    private const int MinWritesToHashInParallel = 4;

    // No trees means nothing was written, so there is nothing to commit.
    public void CommitTree() => Volatile.Read(ref _trees)?.Tree.Commit();

    /// <summary>Whether the storage trie holds nodes written since its last commit.</summary>
    /// <remarks>A trie no write batch took over holds only speculative writes the block never reported, so it has none.</remarks>
    internal bool HasUncommittedNodes =>
        (Volatile.Read(ref _speculativeQueue) is null || _speculationFinalized) && Volatile.Read(ref _trees)?.Tree.RootRef is { IsDirty: true };

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);

        // The batches are created serially before the parallel commit loop, so the join waits inside the batch's
        // first use on a worker instead of stalling every contract behind the slowest runner here.
        StorageTree tree = GetTrees().Tree;
        Dictionary<UInt256, UInt256>? earlyApplied = AdoptEarlyTree(tree);
        // Deferred, the batch only hashes the tree and the scope commit writes its nodes after the block is reported
        // valid. The hash then goes parallel from the size at which a commit would split the tree across threads.
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(estimatedEntries, tree, onRootUpdated, _address,
            commit: !_config.DeferStorageTrieCommit, minWritesToHashInParallel: MinWritesToHashInParallel);
        return earlyApplied is null
            ? new StorageTreeBulkWriteBatch(trieBatch, this)
            : new EarlyAppliedStorageWriteBatch(trieBatch, this, earlyApplied);
    }

    /// <summary>Whether finalization has claimed the trie from the runner.</summary>
    internal bool SpeculationOwnedByFinalization => Volatile.Read(ref _speculationState) == SpeculationState.Owned;

    /// <summary>Whether a write was ever handed to the speculation workers. For tests.</summary>
    internal bool UsedSpeculation => Volatile.Read(ref _speculativeQueue) is not null;

    /// <summary>Whether every write handed to the speculation workers has been drained. For tests.</summary>
    internal bool SpeculationDrained => Volatile.Read(ref _speculativeQueue) is not { IsEmpty: false } && Volatile.Read(ref _speculationState) == SpeculationState.Idle;

    /// <summary>Whether a write was ever handed to the early apply. For tests.</summary>
    internal bool UsedEarlyApply => Volatile.Read(ref _earlyWrites) is not null;

    private readonly struct SpeculativeWrite(in UInt256 index, in UInt256 value)
    {
        public readonly UInt256 Index = index;
        public readonly UInt256 Value = value;
    }

    // For a tree that already holds the early writes: unchanged slots only update the flat overlay.
    private sealed class EarlyAppliedStorageWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch,
        FlatStorageTree storageTree,
        Dictionary<UInt256, UInt256> earlyApplied) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private Dictionary<UInt256, UInt256>? _earlyApplied = earlyApplied;
        private int _reused;

        public void Set(in UInt256 index, in UInt256 value)
        {
            storageTree.Set(index, value);
            if (_earlyApplied is not null && _earlyApplied.Remove(index, out UInt256 applied) && applied == value)
            {
                trieBatch.MarkSet();
                _reused++;
                return;
            }

            trieBatch.Set(in index, value);
        }

        public void Clear()
        {
            // Clearing resets the tree, early writes included.
            _earlyApplied = null;
            trieBatch.Clear();
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            int restored = 0;
            if (_earlyApplied is { Count: > 0 } leftovers)
            {
                // Early writes the block ended at their pre-block value, which the flush skips.
                foreach (UInt256 slot in leftovers.Keys)
                {
                    storageTree.Get(slot, out UInt256 value);
                    trieBatch.Set(slot, value);
                    restored++;
                }
            }

            storageTree._scope.CountEarlyReconciled(_reused, restored);
            trieBatch.Dispose();
        }
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
            storageTree._speculationFinalized = true;
            // Only the block's first batch reconciles the applied writes; a later one finds the trie this one left.
            Dictionary<UInt256, UInt256>? applied = storageTree._speculativelyApplied;
            storageTree._speculativelyApplied = null;
            if (applied is { Count: > 0 })
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
