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

    public bool IsRevealed => _accountTrie is not null;

    public SparsePatriciaTree AccountTrie => _accountTrie ??= new SparsePatriciaTree();

    public SparsePatriciaTree GetOrCreateStorageTrie(Hash256 accountPathHash) =>
        _storageTries.GetOrAdd(accountPathHash, static _ => new SparsePatriciaTree());

    /// <summary>
    /// Live view over storage tries. Caller must not mutate the dictionary during enumeration.
    /// Used by the snapshot committer to persist dirty storage nodes.
    /// </summary>
    public ConcurrentDictionary<Hash256, SparsePatriciaTree> StorageTries => _storageTries;

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
        Dictionary<Hash256, LeafUpdate> updates,
        Action<Hash256, byte>? proofRequired)
    {
        SparsePatriciaTree storageTrie = GetOrCreateStorageTrie(accountPathHash);
        storageTrie.UpdateLeaves(updates, proofRequired);
    }

    public void UpdateAccountLeaves(
        Dictionary<Hash256, LeafUpdate> updates,
        Action<Hash256, byte>? proofRequired) =>
        AccountTrie.UpdateLeaves(updates, proofRequired);

    public Hash256 ComputeStorageRoot(Hash256 accountPathHash)
    {
        if (!_storageTries.TryGetValue(accountPathHash, out SparsePatriciaTree? trie))
            return Keccak.EmptyTreeHash;
        return trie.ComputeRoot();
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
    /// </summary>
    public void SetHotCacheCapacities(int maxHotAccounts, int maxHotSlots)
    {
        _hotAccountsLfu ??= new BucketedLfu<Hash256>(maxHotAccounts);
        _hotSlotsLfu ??= new BucketedLfu<(Hash256, Hash256)>(maxHotSlots);
    }

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
    public void Prune(int maxHotAccounts, int maxHotSlots)
    {
        // M3 stub: LFU decay and evict, then prune account trie and storage tries.
        // Full implementation requires SparsePatriciaTree.Prune(retainedLeaves) which
        // collapses non-retained paths to Blinded. Deferred to full M3 integration.
        _hotAccountsLfu?.DecayAndEvict(maxHotAccounts);
        _hotSlotsLfu?.DecayAndEvict(maxHotSlots);
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
