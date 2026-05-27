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
    internal Dictionary<Hash256, LeafUpdate>? LastAccountChanges => _accountChanges;

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

        Hash256? lastTarget = null;
        int sameTargetCount = 0;
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            List<Hash256> targets = [];
            _trie.UpdateAccountLeaves(_accountChanges, (key, _) => targets.Add(key));
            if (targets.Count == 0) break;

            // Detect stuck-on-same-target case: track if the same target keeps coming back
            Hash256 firstTarget = targets[0];
            if (lastTarget is not null && lastTarget == firstTarget) sameTargetCount++;
            else sameTargetCount = 0;
            lastTarget = firstTarget;

            if (retry == MaxRetries - 1)
            {
                // Walk the sparse trie along the stuck target's nibble path and
                // find the FIRST blinded node — that's what's not being revealed.
                string stuckTrace = WalkSparsePath(firstTarget);
                int singleTargetProofCount = -1;
                try
                {
                    DecodedMultiProof singleProof = MultiProofReader.ReadAccountProofs(
                        _reader, _previousStateRoot, [firstTarget]);
                    singleTargetProofCount = singleProof.AccountNodes.Count;
                }
                catch { /* swallow */ }

                // Re-invoke UpdateLeaves for just the stuck target to capture the exact proofTarget
                string singleUpdateResult = "?";
                try
                {
                    byte[] nibbles = Nibbles.BytesToNibbleBytes(firstTarget.Bytes);
                    Dictionary<Hash256, LeafUpdate> single = new() { [firstTarget] = _accountChanges[firstTarget] };
                    TreePath capturedTarget = default;
                    string capturedKey = "?";
                    _trie.AccountTrie.UpdateLeaves(single, (k, _) =>
                    {
                        capturedKey = k.ToString();
                        capturedTarget = TreePath.FromNibble(Nibbles.BytesToNibbleBytes(k.Bytes));
                    });
                    LeafUpdate upd = _accountChanges[firstTarget];
                    singleUpdateResult = $"updateKind={upd.Kind},capturedKey={capturedKey}";
                }
                catch (Exception singleEx)
                {
                    singleUpdateResult = $"single-exception:{singleEx.GetType().Name}:{singleEx.Message}";
                }

                throw new TrieException(
                    $"Sparse trie account retry loop exceeded {MaxRetries} iterations. " +
                    $"{targets.Count} blinded targets remain. " +
                    $"firstTarget={firstTarget}, sameTargetForLast={sameTargetCount} retries, prevRoot={_previousStateRoot}, " +
                    $"totalChanges={_accountChanges.Count}, lastProofNodeCount={LastProofNodeCount}, " +
                    $"singleTargetProofNodes={singleTargetProofCount}, sparseTrieWalk=[{stuckTrace}], " +
                    $"singleUpdate=[{singleUpdateResult}]");
            }

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

    /// <summary>
    /// Diagnostic helper: walks the sparse account trie along the given target's nibble path,
    /// returning a short trace describing the structure at each level. Stops at the first
    /// blinded node (which is the source of the retry-fail) or when the target's path ends.
    /// </summary>
    private string WalkSparsePath(Hash256 target)
    {
        try
        {
            SparseSubtrie sub = _trie.AccountTrie.Subtrie;
            if (sub.Root < 0) return "EMPTY";

            byte[] nibbles = Nibbles.BytesToNibbleBytes(target.Bytes);
            int nibblePos = 0;
            int nodeIdx = sub.Root;
            System.Text.StringBuilder sb = new();
            int safety = 0;
            while (safety++ < 100 && nibblePos < nibbles.Length)
            {
                SparseTrieNode node = sub.NodeAt(nodeIdx);
                if (node.IsBlinded()) { sb.Append($"[d{nibblePos}:BLINDED]"); break; }
                if (node.IsEmpty()) { sb.Append($"[d{nibblePos}:Empty]"); break; }
                if (node.IsLeaf()) { sb.Append($"[d{nibblePos}:Leaf,shortKey.len={node.ShortKey?.Length ?? 0}]"); break; }
                if (!node.IsBranch()) { sb.Append($"[d{nibblePos}:UnknownKind={node.Kind}]"); break; }

                byte[] shortKey = node.ShortKey ?? [];
                sb.Append($"[d{nibblePos}:Branch,sk={shortKey.Length},mask={node.StateMask},bMask={node.BlindedMask}]");

                if (shortKey.Length > 0)
                {
                    int sharedLen = 0;
                    int limit = Math.Min(shortKey.Length, nibbles.Length - nibblePos);
                    while (sharedLen < limit && nibbles[nibblePos + sharedLen] == shortKey[sharedLen]) sharedLen++;
                    if (sharedLen < shortKey.Length)
                    {
                        sb.Append($"[shortKeyDiverge@{sharedLen}of{shortKey.Length}]");
                        break;
                    }
                    nibblePos += shortKey.Length;
                }
                if (nibblePos >= nibbles.Length) { sb.Append("[pathEnd]"); break; }

                byte nibble = nibbles[nibblePos];
                if (!node.StateMask.IsBitSet(nibble))
                {
                    sb.Append($"[nibble{nibble}:notInMask]");
                    break;
                }
                int denseIdx = sub.NodeAt(nodeIdx).DenseChildIndex(nibble);
                SparseChildEntry entry = sub.ChildAt(denseIdx);
                if (entry.IsBlinded)
                {
                    sb.Append($"[nibble{nibble}:BLINDED]");
                    break;
                }
                nodeIdx = entry.ArenaIndex;
                nibblePos++;
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"walk-exception:{ex.GetType().Name}:{ex.Message}";
        }
    }
}
