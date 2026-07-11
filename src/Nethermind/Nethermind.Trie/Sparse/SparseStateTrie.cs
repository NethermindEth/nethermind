// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public bool IsRevealed => _accountTrie is not null;

    public SparsePatriciaTree AccountTrie => _accountTrie ??= new SparsePatriciaTree();

    public SparsePatriciaTree GetOrCreateStorageTrie(Hash256 accountPathHash) =>
        _storageTries.GetOrAdd(accountPathHash, static _ => new SparsePatriciaTree());

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

    /// <summary>Applies only <paramref name="keysToApply"/> from <paramref name="updates"/> —
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

    public Hash256 ComputeStorageRoot(Hash256 accountPathHash) =>
        ComputeStorageRoot(accountPathHash, allowParallel: true);

    /// <summary>
    /// Computes one storage root, optionally allowing parallel hashing inside that trie.
    /// </summary>
    public Hash256 ComputeStorageRoot(Hash256 accountPathHash, bool allowParallel)
    {
        if (!_storageTries.TryGetValue(accountPathHash, out SparsePatriciaTree? trie))
            return Keccak.EmptyTreeHash;
        return trie.ComputeRoot(allowParallel);
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
        if (_storageTries.TryRemove(accountPathHash, out SparsePatriciaTree? trie))
            trie.Dispose();
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
    }
}
