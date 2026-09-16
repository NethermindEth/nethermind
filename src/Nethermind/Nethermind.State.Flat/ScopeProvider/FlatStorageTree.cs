// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
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
    // and the changed paths hashed on a pool thread while later transactions execute. The block thread is the only
    // producer, one runner at a time consumes, and the block-end write batch joins before it touches the trie.
    private readonly bool _speculate;
    private ConcurrentQueue<SpeculativeWrite>? _speculativeQueue;
    private Dictionary<UInt256, UInt256>? _speculativelyApplied;
    private int _speculationRunning;
    private bool _speculationClosed;
    private volatile bool _speculationFailed;
    private Hash256 _speculationBaseRoot;

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
        _speculate = config.SpeculativeStorageRoots && !scope.Trieless && !config.VerifyWithTrie;
        _speculationBaseRoot = storageRoot;
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
        if (!_speculate || _speculationClosed)
        {
            WarmUpSlot(index);
            return;
        }

        if (_speculativeQueue is null)
        {
            _speculativeQueue = new();
            _speculativelyApplied = [];
            _scope.RegisterSpeculating(this);
        }

        _speculativeQueue.Enqueue(new SpeculativeWrite(in index, in value));
        if (Interlocked.CompareExchange(ref _speculationRunning, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static tree => tree.RunSpeculation(), this, preferLocal: false);
        }
    }

    private void RunSpeculation()
    {
        ConcurrentQueue<SpeculativeWrite> queue = _speculativeQueue!;
        do
        {
            ApplySpeculativeWrites(queue);
            Volatile.Write(ref _speculationRunning, 0);
        }
        // A write enqueued after the drain but before the flag dropped would otherwise wait for the next one.
        while (!queue.IsEmpty && Interlocked.CompareExchange(ref _speculationRunning, 1, 0) == 0);
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
            Dictionary<UInt256, UInt256> applied = _speculativelyApplied!;
            Unsafe.SkipInit(out EvmWord buffer);
            int count = 0;
            while (queue.TryDequeue(out SpeculativeWrite write))
            {
                UInt256 value = write.Value;
                _tree.Set(in write.Index, value.IsZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref buffer));
                applied[write.Index] = value;
                count++;
            }

            if (count > 0) _tree.UpdateRootHash(canBeParallel: count > 64);
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

    /// <summary>Waits for the speculative runner and rewinds the trie if any of its work failed.</summary>
    /// <remarks>
    /// Nothing is enqueued after the block's last commit, so an idle runner means the queue stays empty. Safe to call
    /// more than once and from the write batch's worker threads; after it, the trie belongs to the caller.
    /// </remarks>
    internal void JoinSpeculation()
    {
        if (_speculativeQueue is null) return;
        _speculationClosed = true;

        SpinWait spinWait = new();
        while (Volatile.Read(ref _speculationRunning) != 0) spinWait.SpinOnce();

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

        JoinSpeculation();
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(estimatedEntries, _tree, onRootUpdated, _address, commit: true);
        return new StorageTreeBulkWriteBatch(trieBatch, this, _speculativelyApplied is { Count: > 0 } ? _speculativelyApplied : null);
    }

    private readonly struct SpeculativeWrite(in UInt256 index, in UInt256 value)
    {
        public readonly UInt256 Index = index;
        public readonly UInt256 Value = value;
    }

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch,
        FlatStorageTree storageTree,
        Dictionary<UInt256, UInt256>? speculativelyApplied) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private Dictionary<UInt256, UInt256>? _speculativelyApplied = speculativelyApplied;
        // Slots the runner wrote that the batch has not confirmed: the provider skips a slot the block wrote back to
        // its pre-block value, so whatever is left here at dispose has to be put back to that value.
        private HashSet<UInt256>? _unconfirmed = speculativelyApplied is null ? null : [.. speculativelyApplied.Keys];

        public void Set(in UInt256 index, in UInt256 value)
        {
            if (_speculativelyApplied is not null && _speculativelyApplied.TryGetValue(index, out UInt256 applied))
            {
                _unconfirmed!.Remove(index);
                if (applied == value)
                {
                    storageTree.Set(index, value);
                    return;
                }
            }

            trieBatch.Set(in index, value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            trieBatch.Clear();
            storageTree.ClearStorage();
            _speculativelyApplied = null;
            _unconfirmed = null;
        }

        public void Dispose()
        {
            if (_unconfirmed is { Count: > 0 })
            {
                foreach (UInt256 index in _unconfirmed)
                {
                    storageTree.GetPreBlockSlot(in index, out UInt256 original);
                    trieBatch.Set(in index, original);
                }
            }

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
