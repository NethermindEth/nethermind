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
/// <remarks>
/// Before applying leaf updates, existing trie structure is revealed via multiproofs.
/// Accepts an external <see cref="SparseStateTrie"/> for cross-block reuse (M3).
/// </remarks>
/// </summary>
public sealed class SparseRootComputer : IDisposable
{
    private const int MaxRetries = 10;

    private readonly SparseStateTrie _trie;
    private readonly ITrieNodeReader _reader;
    private readonly Hash256 _previousStateRoot;
    private readonly bool _ownsTrie;
    private readonly Dictionary<Hash256, (Hash256 PreviousStorageRoot, Dictionary<Hash256, LeafUpdate> Updates)> _storageChanges = [];
    private Dictionary<Hash256, LeafUpdate>? _accountChanges;

    public SparseRootComputer(ITrieNodeReader reader, Hash256 previousStateRoot)
        : this(new SparseStateTrie(), reader, previousStateRoot, ownsTrie: true) { }

    public SparseRootComputer(SparseStateTrie trie, ITrieNodeReader reader, Hash256 previousStateRoot)
        : this(trie, reader, previousStateRoot, ownsTrie: false) { }

    private SparseRootComputer(SparseStateTrie trie, ITrieNodeReader reader, Hash256 previousStateRoot, bool ownsTrie)
    {
        _trie = trie;
        _reader = reader;
        _previousStateRoot = previousStateRoot;
        _ownsTrie = ownsTrie;
    }

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

        if (entry.PreviousStorageRoot != Keccak.EmptyTreeHash)
        {
            Hash256[] targetKeys = entry.Updates.Keys.ToArray();
            DecodedMultiProof initialProof = MultiProofReader.ReadStorageProofs(
                _reader, accountPathHash, entry.PreviousStorageRoot, targetKeys);
            if (initialProof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? initialNodes))
                storageTrie.RevealNodes(initialNodes);
        }

        Dictionary<Hash256, LeafUpdate> updates = entry.Updates;
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            List<Hash256> targets = [];
            storageTrie.UpdateLeaves(updates, (key, _) => targets.Add(key));
            if (targets.Count == 0) break;

            if (retry == MaxRetries - 1)
                throw new TrieException($"Sparse trie storage retry loop exceeded {MaxRetries} iterations for account {accountPathHash}. {targets.Count} blinded targets remain.");

            DecodedMultiProof proof = MultiProofReader.ReadStorageProofs(
                _reader, accountPathHash, entry.PreviousStorageRoot, targets.ToArray());
            if (proof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? nodes))
                storageTrie.RevealNodes(nodes);
        }

        return _trie.ComputeStorageRoot(accountPathHash);
    }

    public Hash256 PreviousRoot => _previousStateRoot;
    public int AccountChangeCount => _accountChanges?.Count ?? 0;
    public int LastProofNodeCount { get; private set; }

    /// <summary>The underlying trie for persistence and cross-block storage.</summary>
    internal SparseStateTrie Trie => _trie;

    public Hash256 ComputeStateRoot()
    {
        if (_accountChanges is null || _accountChanges.Count == 0)
            return _previousStateRoot;

        Hash256[] targetKeys = _accountChanges.Keys.ToArray();
        DecodedMultiProof initialProof = MultiProofReader.ReadAccountProofs(
            _reader, _previousStateRoot, targetKeys);
        LastProofNodeCount = initialProof.AccountNodes.Count;

        _trie.AccountTrie.RevealNodes(initialProof.AccountNodes);

        for (int retry = 0; retry < MaxRetries; retry++)
        {
            List<Hash256> targets = [];
            _trie.UpdateAccountLeaves(_accountChanges, (key, _) => targets.Add(key));
            if (targets.Count == 0) break;

            if (retry == MaxRetries - 1)
                throw new TrieException($"Sparse trie account retry loop exceeded {MaxRetries} iterations. {targets.Count} blinded targets remain.");

            DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
                _reader, _previousStateRoot, targets.ToArray());
            _trie.AccountTrie.RevealNodes(proof.AccountNodes);
        }

        return _trie.ComputeRoot();
    }

    public void Dispose()
    {
        if (_ownsTrie) _trie.Dispose();
    }
}
