// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Orchestrates proof-based state root computation for a single block using the sparse trie.
/// M2 hybrid: computes root only — persistence is handled by Patricia tree.
/// <remarks>
/// The key insight: before applying leaf updates, we MUST first reveal the existing trie
/// structure via multiproofs. Without this, the sparse trie treats everything as inserts
/// into an empty trie and computes a root from only the changed keys — ignoring millions
/// of existing accounts/slots whose sibling hashes are needed for the correct root.
/// </remarks>
/// </summary>
public sealed class SparseRootComputer(ITrieNodeReader reader, Hash256 previousStateRoot) : IDisposable
{
    private readonly SparseStateTrie _trie = new();
    private readonly Dictionary<Hash256, (Hash256 PreviousStorageRoot, Dictionary<Hash256, LeafUpdate> Updates)> _storageChanges = [];
    private Dictionary<Hash256, LeafUpdate>? _accountChanges;

    public void AddStorageChanges(Hash256 accountPathHash, Hash256 previousStorageRoot, Dictionary<Hash256, LeafUpdate> slotUpdates) =>
        _storageChanges[accountPathHash] = (previousStorageRoot, slotUpdates);

    public void SetAccountChanges(Dictionary<Hash256, LeafUpdate> accountUpdates) =>
        _accountChanges = accountUpdates;

    public Hash256 ComputeStorageRoot(Hash256 accountPathHash)
    {
        if (!_storageChanges.TryGetValue(accountPathHash, out (Hash256 PreviousStorageRoot, Dictionary<Hash256, LeafUpdate> Updates) entry))
            return Keccak.EmptyTreeHash;

        if (entry.PreviousStorageRoot == Keccak.EmptyTreeHash && entry.Updates.Count == 0)
            return Keccak.EmptyTreeHash;

        SparsePatriciaTree storageTrie = _trie.GetOrCreateStorageTrie(accountPathHash);

        // Step 1: Reveal existing trie structure for ALL changed keys BEFORE any updates
        if (entry.PreviousStorageRoot != Keccak.EmptyTreeHash)
        {
            Hash256[] targetKeys = entry.Updates.Keys.ToArray();
            DecodedMultiProof initialProof = MultiProofReader.ReadStorageProofs(
                reader, accountPathHash, entry.PreviousStorageRoot, targetKeys);
            if (initialProof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? initialNodes))
                storageTrie.RevealNodes(initialNodes);
        }

        // Step 2: Apply updates (may need additional reveals for structural changes)
        Dictionary<Hash256, LeafUpdate> updates = entry.Updates;
        while (true)
        {
            List<Hash256> targets = [];
            storageTrie.UpdateLeaves(updates, (key, _) => targets.Add(key));
            if (targets.Count == 0) break;

            DecodedMultiProof proof = MultiProofReader.ReadStorageProofs(
                reader, accountPathHash, entry.PreviousStorageRoot, targets.ToArray());
            if (proof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? nodes))
                storageTrie.RevealNodes(nodes);
        }

        return _trie.ComputeStorageRoot(accountPathHash);
    }

    public Hash256 PreviousRoot => previousStateRoot;
    public int AccountChangeCount => _accountChanges?.Count ?? 0;
    public int LastProofNodeCount { get; private set; }

    public Hash256 ComputeStateRoot()
    {
        if (_accountChanges is null || _accountChanges.Count == 0)
            return previousStateRoot;

        Hash256[] targetKeys = _accountChanges.Keys.ToArray();
        DecodedMultiProof initialProof = MultiProofReader.ReadAccountProofs(
            reader, previousStateRoot, targetKeys);
        LastProofNodeCount = initialProof.AccountNodes.Count;

        _trie.AccountTrie.RevealNodes(initialProof.AccountNodes);

        while (true)
        {
            List<Hash256> targets = [];
            _trie.UpdateAccountLeaves(_accountChanges, (key, _) => targets.Add(key));
            if (targets.Count == 0) break;

            DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
                reader, previousStateRoot, targets.ToArray());
            _trie.AccountTrie.RevealNodes(proof.AccountNodes);
        }

        return _trie.ComputeRoot();
    }

    public void Dispose() => _trie.Dispose();
}
