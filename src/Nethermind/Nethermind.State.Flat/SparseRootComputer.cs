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
            Hash256[] targetKeys = new Hash256[entry.Updates.Count];
            int i = 0;
            foreach (Hash256 k in entry.Updates.Keys) targetKeys[i++] = k;
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
    public long LastProofReadMs { get; private set; }
    public long LastRevealMs { get; private set; }
    public long LastUpdateLeavesMs { get; private set; }
    public long LastComputeRootMs { get; private set; }
    public int LastRetryCount { get; private set; }
    internal Dictionary<Hash256, LeafUpdate>? LastAccountChanges => _accountChanges;

    /// <summary>The underlying trie for persistence and cross-block storage.</summary>
    internal SparseStateTrie Trie => _trie;

    public Hash256 ComputeStateRoot()
    {
        if (_accountChanges is null || _accountChanges.Count == 0)
            return _previousStateRoot;

        Hash256[] targetKeys = new Hash256[_accountChanges.Count];
        {
            int i = 0;
            foreach (Hash256 k in _accountChanges.Keys) targetKeys[i++] = k;
        }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        DecodedMultiProof initialProof = MultiProofReader.ReadAccountProofs(
            _reader, _previousStateRoot, targetKeys);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastProofReadMs = (t1 - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        LastProofNodeCount = initialProof.AccountNodes.Count;

        _trie.AccountTrie.RevealNodes(initialProof.AccountNodes);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastRevealMs = (t2 - t1) * 1000 / System.Diagnostics.Stopwatch.Frequency;

        long updateMsAccum = 0;
        Hash256? lastTarget = null;
        int sameTargetCount = 0;
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            List<(Hash256 key, byte minLen)> targets = [];
            _trie.UpdateAccountLeaves(_accountChanges, (key, minLen) => targets.Add((key, minLen)));
            updateMsAccum += (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            LastRetryCount = retry;
            if (targets.Count == 0) break;

            // Detect stuck-on-same-target case: track if the same target keeps coming back
            Hash256 firstTarget = targets[0].key;
            if (lastTarget is not null && lastTarget == firstTarget) sameTargetCount++;
            else sameTargetCount = 0;
            lastTarget = firstTarget;

            // For deletion-with-blinded-sibling: the proof reader doesn't fetch the sibling
            // (no target walks through that nibble). Resolve the sibling directly from the
            // sparse trie's known blinded hashes and inject it as a ProofNode.
            if (sameTargetCount >= 1)
            {
                List<Hash256> deletionTargets = [];
                foreach ((Hash256 k, _) in targets) deletionTargets.Add(k);
                TryResolveBlindedSiblings(deletionTargets);
            }

            if (retry == MaxRetries - 1)
                throw new TrieException(
                    $"Sparse trie account retry loop exceeded {MaxRetries} iterations. " +
                    $"{targets.Count} blinded targets remain. firstTarget={firstTarget}, " +
                    $"prevRoot={_previousStateRoot}, totalChanges={_accountChanges.Count}");

            // Pass per-target minLen so the proof reader can SKIP adding nodes ABOVE that
            // depth (sparse trie already has them revealed). Major win for cross-block reuse.
            Hash256[] tArr = new Hash256[targets.Count];
            byte[] minLens = new byte[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                tArr[i] = targets[i].key;
                minLens[i] = targets[i].minLen;
            }
            DecodedMultiProof proof = MultiProofReader.ReadAccountProofs(
                _reader, _previousStateRoot, tArr, minLens);
            _trie.AccountTrie.RevealNodes(proof.AccountNodes);
        }

        LastUpdateLeavesMs = updateMsAccum;
        long tc = System.Diagnostics.Stopwatch.GetTimestamp();
        Hash256 root = _trie.ComputeRoot();
        LastComputeRootMs = (System.Diagnostics.Stopwatch.GetTimestamp() - tc) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        return root;
    }

    public void Dispose()
    {
        if (_ownsTrie) _trie.Dispose();
    }

    /// <summary>
    /// For deletion targets whose collapse would need a blinded sibling, fetch the sibling
    /// directly via the reader (using the hash stored as the blinded child's RlpNode) and
    /// reveal it. This unsticks the retry loop for blinded-sibling-deletion cases.
    /// </summary>
    private void TryResolveBlindedSiblings(List<Hash256> targets)
    {
        foreach (Hash256 target in targets)
        {
            if (!_accountChanges!.TryGetValue(target, out LeafUpdate upd) || !upd.IsDelete)
                continue;

            byte[] nibbles = Nibbles.BytesToNibbleBytes(target.Bytes);
            if (!_trie.AccountTrie.Subtrie.TryFindBlindedSiblingForDeletion(nibbles, out TreePath siblingPath, out RlpNode siblingRlpNode))
                continue;

            if (!siblingRlpNode.IsHash())
                continue; // inline siblings are already known via the parent's RLP

            try
            {
                Hash256 siblingHash = siblingRlpNode.AsHash();
                byte[] siblingRlp = _reader.LoadStateRlp(siblingPath, siblingHash);
                ProofNode siblingProof = MultiProofReader.DecodeProofNode(siblingRlp, siblingPath);
                _trie.AccountTrie.RevealNodes([siblingProof]);
            }
            catch (MissingTrieNodeException) { /* sibling missing in DB — outer retry will throw with diagnostics */ }
            catch (TrieNodeHashMismatchException) { /* same as above */ }
        }
    }

}
