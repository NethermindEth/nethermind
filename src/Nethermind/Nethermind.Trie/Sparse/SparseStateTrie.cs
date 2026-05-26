// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    /// <summary>Storage tries keyed by accountPathHash (keccak(address)).</summary>
    private readonly Dictionary<Hash256, SparsePatriciaTree> _storageTries = [];

    public bool IsRevealed => _accountTrie is not null;

    public SparsePatriciaTree AccountTrie => _accountTrie ??= new SparsePatriciaTree();

    public SparsePatriciaTree GetOrCreateStorageTrie(Hash256 accountPathHash)
    {
        if (!_storageTries.TryGetValue(accountPathHash, out SparsePatriciaTree? trie))
        {
            trie = new SparsePatriciaTree();
            _storageTries[accountPathHash] = trie;
        }
        return trie;
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

    public void Clear()
    {
        _accountTrie?.Clear();
        _accountTrie = null;
        foreach (SparsePatriciaTree trie in _storageTries.Values)
            trie.Dispose();
        _storageTries.Clear();
    }

    public void Dispose()
    {
        _accountTrie?.Dispose();
        foreach (SparsePatriciaTree trie in _storageTries.Values)
            trie.Dispose();
        _storageTries.Clear();
    }
}
