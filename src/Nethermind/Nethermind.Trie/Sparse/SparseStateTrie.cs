// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Combines an account trie and per-account storage tries into a single state trie.
/// Handles the ComputeRoot flow: storage roots first, then account updates, then account root.
/// </summary>
public sealed class SparseStateTrie : IDisposable
{
    private SparsePatriciaTree? _accountTrie;

    /// <summary>
    /// Storage tries keyed by accountPathHash (keccak(address)). ConcurrentDictionary because
    /// PersistentStorageProvider.UpdateRootHashesMultiThread calls per-contract Dispose in
    /// parallel; GetOrCreateStorageTrie must be thread-safe to avoid dictionary corruption.
    /// Per-contract SparsePatriciaTree mutation remains exclusive to that contract's batch.
    /// </summary>
    private readonly ConcurrentDictionary<Hash256, SparsePatriciaTree> _storageTries = new();

    private BucketedLfu<Hash256>? _hotAccountsLfu;
    private BucketedLfu<(Hash256, Hash256)>? _hotSlotsLfu;

    // Pool of cleared storage tries kept for reuse instead of being GC'd when pruning drops a
    // cold contract. SparsePatriciaTree.Clear() wipes the arena back to empty but RETAINS the
    // allocated backing arrays, so a pooled instance hands a freshly-touched contract a warm
    // arena and avoids re-growing from the initial capacity. Mirrors Reth keeping cleared
    // storage tries. Bounded so a one-off churn spike can't pin unbounded memory in the pool.
    private const int MaxPooledStorageTries = 1024;
    private readonly ConcurrentBag<SparsePatriciaTree> _storageTriePool = [];

    public bool IsRevealed => _accountTrie is not null;

    public SparsePatriciaTree AccountTrie => _accountTrie ??= new SparsePatriciaTree();

    public SparsePatriciaTree GetOrCreateStorageTrie(Hash256 accountPathHash) =>
        _storageTries.GetOrAdd(accountPathHash, RentStorageTrie);

    private SparsePatriciaTree RentStorageTrie(Hash256 _) =>
        _storageTriePool.TryTake(out SparsePatriciaTree? pooled) ? pooled : new SparsePatriciaTree();

    // A pooled trie keeps its backing arena arrays for reuse. A one-off huge contract (e.g. a
    // mega-airdrop touching 100k+ slots) would otherwise pin a giant arena in the pool forever,
    // defeating the memory bound. Above this arena high-water we dispose instead of pooling, so a
    // fresh contract rents a right-sized arena rather than inheriting the bloated one. This is the
    // safe analogue of Reth's shrink_nodes_to â€” it bounds pooled memory without the index
    // remapping that in-place arena compaction would require.
    private const int MaxPooledArenaHighWater = 16_384;

    /// <summary>Returns a no-longer-referenced storage trie to the pool (cleared) instead of
    /// dropping it, unless the pool is at capacity or the trie's arena grew oversized, in which
    /// case it is disposed.</summary>
    private void ReturnStorageTrieToPool(SparsePatriciaTree trie)
    {
        if (_storageTriePool.Count >= MaxPooledStorageTries || trie.ArenaHighWater > MaxPooledArenaHighWater)
        {
            trie.Dispose();
            return;
        }
        trie.Clear(); // wipe contents, keep backing arrays for reuse
        _storageTriePool.Add(trie);
    }

    /// <summary>
    /// Live view over storage tries. Caller must not mutate the dictionary during enumeration.
    /// Used by the snapshot committer to persist dirty storage nodes.
    /// </summary>
    public ConcurrentDictionary<Hash256, SparsePatriciaTree> StorageTries => _storageTries;

    /// <summary>Snapshot of the cross-block cache's retained size, for observability/metrics.</summary>
    public readonly record struct CacheSize(int StorageTrieCount, int AccountArenaNodes, long StorageArenaNodes);

    /// <summary>
    /// Reports the preserved trie's retained footprint so operators can watch cross-block
    /// cache growth and size the LFU prune caps. <see cref="CacheSize.StorageTrieCount"/> is the
    /// number of contracts whose storage subtrie is currently held in memory (this is the value
    /// that grows unbounded when pruning is off); the arena-node counts are cheap high-water
    /// proxies for the account trie and the sum across all storage tries.
    /// </summary>
    public CacheSize GetCacheSize()
    {
        int accountNodes = _accountTrie?.ArenaHighWater ?? 0;
        long storageNodes = 0;
        foreach (KeyValuePair<Hash256, SparsePatriciaTree> kvp in _storageTries)
            storageNodes += kvp.Value.ArenaHighWater;
        return new CacheSize(_storageTries.Count, accountNodes, storageNodes);
    }

    public void RevealMultiproof(DecodedMultiProof proof)
    {
        if (proof.AccountNodes.Count > 0)
            AccountTrie.RevealNodes(proof.AccountNodes);

        foreach (KeyValuePair<Hash256, List<ProofNode>> kvp in proof.StorageNodes)
        {
            SparsePatriciaTree storageTrie = GetOrCreateStorageTrie(kvp.Key);
            storageTrie.RevealNodes(kvp.Value);
        }
    }

    public void UpdateStorageLeaves(
        Hash256 accountPathHash,
        Dictionary<ValueHash256, LeafUpdate> updates,
        Action<ValueHash256, byte>? proofRequired)
    {
        SparsePatriciaTree storageTrie = GetOrCreateStorageTrie(accountPathHash);
        storageTrie.UpdateLeaves(updates, proofRequired);
    }

    public void UpdateAccountLeaves(
        Dictionary<ValueHash256, LeafUpdate> updates,
        Action<ValueHash256, byte>? proofRequired) =>
        AccountTrie.UpdateLeaves(updates, proofRequired);

    /// <summary>Applies only <paramref name="keysToApply"/> from <paramref name="updates"/> â€”
    /// used by the retry loop to re-process only the prior pass's blinded misses.</summary>
    public void UpdateAccountLeavesSubset(
        Dictionary<ValueHash256, LeafUpdate> updates,
        Span<ValueHash256> keysToApply,
        Action<ValueHash256, byte>? proofRequired) =>
        AccountTrie.UpdateLeavesSubset(updates, keysToApply, proofRequired);

    /// <summary>Storage counterpart of <see cref="UpdateAccountLeavesSubset"/>.</summary>
    public void UpdateStorageLeavesSubset(
        Hash256 accountPathHash,
        Dictionary<ValueHash256, LeafUpdate> updates,
        Span<ValueHash256> keysToApply,
        Action<ValueHash256, byte>? proofRequired) =>
        GetOrCreateStorageTrie(accountPathHash).UpdateLeavesSubset(updates, keysToApply, proofRequired);

    public Hash256 ComputeStorageRoot(Hash256 accountPathHash)
    {
        if (!_storageTries.TryGetValue(accountPathHash, out SparsePatriciaTree? trie))
            return Keccak.EmptyTreeHash;
        // Storage tries DO run from parallel workers in
        // PersistentStorageProvider.UpdateRootHashesMultiThread â€” but EXPB 26637010048
        // showed that disabling the inner parallelism entirely cost ~60 ms p95. The .NET
        // thread pool deals with nested Parallel.For by sharing workers, so the practical
        // cost of nesting is bounded by the per-call MaxDegreeOfParallelism cap (added in
        // F3). Leave the parameter on the API for callers that hold a Parallel context
        // they own, but default to allowing parallel here.
        return trie.ComputeRoot(allowParallel: true);
    }

    /// <summary>
    /// Computes the full state root.
    /// <remarks>
    /// Caller must have already: (1) computed storage roots for all changed contracts,
    /// (2) re-encoded each changed account with its new storageRoot as LeafUpdate.Changed(rlp),
    /// (3) called UpdateAccountLeaves with those updates.
    /// Storage-only changes must still rewrite the account leaf.
    /// </remarks>
    /// </summary>
    public Hash256 ComputeRoot() => AccountTrie.ComputeRoot();

    public void WipeStorage(Hash256 accountPathHash)
    {
        if (_storageTries.TryGetValue(accountPathHash, out SparsePatriciaTree? trie))
        {
            trie.WipeStorage();
        }
    }

    /// <summary>
    /// Initializes LFU caches for cross-block hot path retention.
    /// Called at the start of each block from PreservedSparseTrie.Take().
    /// When a capacity is int.MaxValue, the corresponding LFU stays null so touches and
    /// pruning are no-ops â€” lets operators turn cross-block hot tracking off entirely.
    /// </summary>
    /// <param name="maxHotAccounts">Per-block LFU account cap (int.MaxValue = LFU disabled).</param>
    /// <param name="maxHotSlots">Per-block LFU slot cap (int.MaxValue = LFU disabled).</param>
    /// <param name="maxRetainedStorageTries">
    /// Memory-trigger budget. When finite, BOTH LFUs must exist even if the per-block caps are
    /// int.MaxValue, otherwise the triggered prune (which retains by LFU frequency) has no
    /// frequency data and storage eviction silently no-ops. The LFUs are sized to the budget so
    /// they can hold the full retained set; per-block eviction still won't fire because the
    /// triggered prune passes its own (budget-derived) caps to <see cref="Prune"/>.
    /// </param>
    public void SetHotCacheCapacities(int maxHotAccounts, int maxHotSlots, int maxRetainedStorageTries = int.MaxValue)
    {
        bool memoryTriggerActive = maxRetainedStorageTries < int.MaxValue;

        if (maxHotAccounts < int.MaxValue)
            _hotAccountsLfu ??= new BucketedLfu<Hash256>(maxHotAccounts);
        else if (memoryTriggerActive)
            _hotAccountsLfu ??= new BucketedLfu<Hash256>(maxRetainedStorageTries);

        if (maxHotSlots < int.MaxValue)
            _hotSlotsLfu ??= new BucketedLfu<(Hash256, Hash256)>(maxHotSlots);
        else if (memoryTriggerActive)
            // Slot budget is unknown; size generously off the contract budget. The triggered
            // prune supplies the actual retention count to Prune(); this only needs to be a live
            // LFU collecting touch frequency so eviction has something to rank by.
            _hotSlotsLfu ??= new BucketedLfu<(Hash256, Hash256)>(maxRetainedStorageTries);
    }

    /// <summary>True when the account-LFU is enabled (cap is &lt; int.MaxValue). Used to
    /// short-circuit per-update touch loops when the default "Prune disabled" config is in
    /// effect â€” iterating the updates dictionary just to call a null-checking no-op shows up
    /// on storage-heavy blocks.</summary>
    public bool HasAccountLfu => _hotAccountsLfu is not null;

    /// <summary>True when the slot-LFU is enabled. See <see cref="HasAccountLfu"/>.</summary>
    public bool HasSlotLfu => _hotSlotsLfu is not null;

    /// <summary>
    /// Touches account and slot keys in the LFU caches during UpdateLeaves.
    /// Call after each UpdateAccountLeaves/UpdateStorageLeaves.
    /// </summary>
    public void TouchLfu(Hash256 accountPathHash) =>
        _hotAccountsLfu?.Touch(accountPathHash);

    public void TouchSlotLfu(Hash256 accountPathHash, Hash256 slotHash) =>
        _hotSlotsLfu?.Touch((accountPathHash, slotHash));

    /// <summary>
    /// Prunes cold paths from the sparse trie based on LFU frequency.
    /// Called after root computation, before storing back as PreservedSparseTrie.
    /// Entries not in the retained set are collapsed back to Blinded stubs.
    /// Storage tries with zero retained slots are cleared and pooled for reuse.
    /// </summary>
    /// <returns>The number of whole storage tries evicted (returned to the pool) this prune.
    /// Mirrors Reth's prune-returns-evicted-count so the trigger site can log/meter how much the
    /// memory bound actually reclaimed.</returns>
    public int Prune(int maxHotAccounts, int maxHotSlots)
    {
        int evictedStorageTries = 0;

        // Step 1: decay the LFU caches down to capacity and snapshot the retained sets.
        _hotAccountsLfu?.DecayAndEvict(maxHotAccounts);
        _hotSlotsLfu?.DecayAndEvict(maxHotSlots);

        if (_accountTrie is not null && _hotAccountsLfu is not null)
        {
            HashSet<HashKey> retained = new(_hotAccountsLfu.Count);
            foreach (Hash256 k in _hotAccountsLfu.RetainedKeys)
                retained.Add(new HashKey(Nibbles.BytesToNibbleBytes(k.Bytes)));
            _accountTrie.Prune(nibbles => retained.Contains(new HashKey(nibbles.ToArray())));
        }

        if (_hotSlotsLfu is not null)
        {
            // Group retained slots by accountPathHash so each storage trie is pruned with a
            // smaller set + a single allocator pass.
            Dictionary<Hash256, HashSet<HashKey>> byAccount = [];
            foreach ((Hash256 acc, Hash256 slot) in _hotSlotsLfu.RetainedKeys)
            {
                if (!byAccount.TryGetValue(acc, out HashSet<HashKey>? set))
                {
                    set = [];
                    byAccount[acc] = set;
                }
                set.Add(new HashKey(Nibbles.BytesToNibbleBytes(slot.Bytes)));
            }
            foreach (KeyValuePair<Hash256, SparsePatriciaTree> kvp in _storageTries)
            {
                if (byAccount.TryGetValue(kvp.Key, out HashSet<HashKey>? retained))
                {
                    kvp.Value.Prune(nibbles => retained.Contains(new HashKey(nibbles.ToArray())));
                }
                else
                {
                    // No retained slots for this contract â€” remove it from the live set and
                    // return the (cleared) trie to the pool for reuse rather than disposing,
                    // avoiding arena re-allocation when a later block touches a fresh contract.
                    if (_storageTries.TryRemove(kvp.Key, out SparsePatriciaTree? dropped))
                    {
                        ReturnStorageTrieToPool(dropped);
                        evictedStorageTries++;
                    }
                }
            }
        }

        return evictedStorageTries;
    }

    /// <summary>Wrapper that gives byte[] structural equality + hash for HashSet membership.</summary>
    private readonly struct HashKey(byte[] bytes) : IEquatable<HashKey>
    {
        private readonly byte[] _bytes = bytes;
        public bool Equals(HashKey other) => _bytes.AsSpan().SequenceEqual(other._bytes);
        public override bool Equals(object? obj) => obj is HashKey k && Equals(k);
        public override int GetHashCode()
        {
            uint h = 2166136261u;
            foreach (byte b in _bytes) { h ^= b; h *= 16777619u; }
            return (int)h;
        }
    }

    public void Clear()
    {
        _accountTrie?.Clear();
        _accountTrie = null;
        foreach (KeyValuePair<Hash256, SparsePatriciaTree> kvp in _storageTries)
            kvp.Value.Dispose();
        _storageTries.Clear();
    }

    public void Dispose()
    {
        _accountTrie?.Dispose();
        foreach (KeyValuePair<Hash256, SparsePatriciaTree> kvp in _storageTries)
            kvp.Value.Dispose();
        _storageTries.Clear();
        // Drain the reuse pool too â€” its tries hold arena backing arrays.
        while (_storageTriePool.TryTake(out SparsePatriciaTree? pooled))
            pooled.Dispose();
    }
}
