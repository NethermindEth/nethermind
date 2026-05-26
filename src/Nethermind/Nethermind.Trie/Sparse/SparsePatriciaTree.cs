// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Sparse Patricia trie backed by a single <see cref="SparseSubtrie"/> arena.
/// For M1: single-tier (no upper/lower split) to ensure correctness.
/// Two-tier parallelism (256 lower subtries) will be added in a follow-up once
/// boundary canonicalization is proven correct.
/// </summary>
public sealed class SparsePatriciaTree : IDisposable
{
    private readonly SparseSubtrie _subtrie = new();

    public bool IsRevealed { get; private set; }

    /// <summary>
    /// Reveals proof nodes into the sparse trie. Proof nodes must be sorted by path
    /// (root first, then children). Each proof node is walked from the root to find
    /// the correct blinded child to replace.
    /// </summary>
    public void RevealNodes(IReadOnlyList<ProofNode> proofNodes)
    {
        foreach (ProofNode pn in proofNodes)
            RevealSingleNode(pn);
        IsRevealed = true;
    }

    private void RevealSingleNode(ProofNode proofNode)
    {
        if (_subtrie.Root == -1)
        {
            _subtrie.Root = CreateNodeFromProof(_subtrie, proofNode);
            return;
        }

        // Walk the existing trie from root to find the parent that should hold this node
        TreePath targetPath = proofNode.Path;
        if (targetPath.Length == 0)
        {
            // Replacing root entirely
            _subtrie.Root = CreateNodeFromProof(_subtrie, proofNode);
            return;
        }

        // Walk down from root following the target path nibbles
        int currentIdx = _subtrie.Root;
        byte[] targetNibbles = targetPath.ToNibble();
        int nibblePos = 0;

        while (nibblePos < targetNibbles.Length)
        {
            ref SparseTrieNode current = ref _subtrie.NodeAt(currentIdx);

            if (current.IsBranch())
            {
                byte[] shortKey = current.ShortKey ?? [];
                // Skip past the extension prefix
                nibblePos += shortKey.Length;
                if (nibblePos >= targetNibbles.Length) break;

                int nibble = targetNibbles[nibblePos];
                if (!current.StateMask.IsBitSet(nibble)) break;

                int denseIdx = current.DenseChildIndex(nibble);
                ref SparseChildEntry childEntry = ref _subtrie.ChildAt(denseIdx);

                nibblePos++;
                if (nibblePos >= targetNibbles.Length || nibblePos == targetPath.Length)
                {
                    // This child is the position where the proof node goes
                    if (childEntry.IsBlinded)
                    {
                        int newNodeIdx = CreateNodeFromProof(_subtrie, proofNode);
                        _subtrie.ChildAt(denseIdx) = SparseChildEntry.Revealed(newNodeIdx);
                        current.BlindedMask = current.BlindedMask.ClearBit(nibble);
                    }
                    return;
                }

                if (childEntry.IsBlinded)
                {
                    // Need to reveal this child first before we can descend
                    // The proof should contain this node too — it will be revealed in order
                    int newNodeIdx = CreateNodeFromProof(_subtrie, proofNode);
                    _subtrie.ChildAt(denseIdx) = SparseChildEntry.Revealed(newNodeIdx);
                    current.BlindedMask = current.BlindedMask.ClearBit(nibble);
                    return;
                }

                currentIdx = childEntry.ArenaIndex;
                continue;
            }

            // Reached a leaf or empty node — can't descend further
            break;
        }
    }

    private static int CreateNodeFromProof(SparseSubtrie subtrie, ProofNode proofNode)
    {
        switch (proofNode.Kind)
        {
            case ProofNodeKind.Leaf:
                {
                    int valIdx = subtrie.AllocValue(proofNode.Value ?? []);
                    SparseTrieNode leaf = SparseTrieNode.CreateLeaf(proofNode.Key ?? [], valIdx);
                    leaf.CachedRlp = RlpNode.FromRlp(proofNode.RawRlp ?? []);
                    leaf.State = SparseNodeState.Cached;
                    int leafIdx = subtrie.AllocNode(leaf);
                    subtrie.NumLeaves++;
                    return leafIdx;
                }

            case ProofNodeKind.Branch:
                {
                    int childCount = proofNode.ChildMask.CountBits();
                    int childStart = subtrie.AllocChildren(childCount);
                    TrieMask mask = proofNode.ChildMask;
                    int dense = 0;
                    for (int n = 0; n < 16; n++)
                    {
                        if (!mask.IsBitSet(n)) continue;
                        RlpNode childRlp = proofNode.ChildRlps is not null && n < proofNode.ChildRlps.Length
                            ? proofNode.ChildRlps[n]
                            : default;
                        subtrie.ChildAt(childStart + dense) = SparseChildEntry.Blinded(childRlp);
                        dense++;
                    }

                    SparseTrieNode branch = proofNode.Key is { Length: > 0 }
                        ? SparseTrieNode.CreateBranchWithExtension(proofNode.Key, mask, childStart)
                        : SparseTrieNode.CreateBranch(mask, childStart);
                    branch.BlindedMask = mask;
                    branch.CachedRlp = RlpNode.FromRlp(proofNode.RawRlp ?? []);
                    branch.State = SparseNodeState.Cached;
                    return subtrie.AllocNode(branch);
                }

            case ProofNodeKind.Extension:
                {
                    // Extensions are represented as branches with a ShortKey
                    // The child nibble comes from the extension's child reference
                    // For a proper extension, we need to decode what the child points to
                    TrieMask mask = TrieMask.Empty;
                    if (proofNode.ChildNibble >= 0)
                        mask = mask.SetBit(proofNode.ChildNibble);

                    int childStart = subtrie.AllocChildren(Math.Max(mask.CountBits(), 0));
                    if (mask.CountBits() > 0)
                    {
                        RlpNode childRlp = proofNode.ChildRlps is not null && proofNode.ChildRlps.Length > 0
                            ? proofNode.ChildRlps[0]
                            : default;
                        subtrie.ChildAt(childStart) = SparseChildEntry.Blinded(childRlp);
                    }

                    SparseTrieNode node = SparseTrieNode.CreateBranchWithExtension(
                        proofNode.Key ?? [], mask, childStart);
                    node.BlindedMask = mask;
                    node.CachedRlp = RlpNode.FromRlp(proofNode.RawRlp ?? []);
                    node.State = SparseNodeState.Cached;
                    return subtrie.AllocNode(node);
                }

            default:
                return subtrie.AllocNode(SparseTrieNode.CreateEmpty());
        }
    }

    /// <summary>
    /// Applies leaf updates to the trie. Updates that hit blinded nodes invoke the callback.
    /// </summary>
    public void UpdateLeaves(
        Dictionary<Hash256, LeafUpdate> updates,
        Action<Hash256, byte>? proofRequired)
    {
        foreach (KeyValuePair<Hash256, LeafUpdate> kvp in updates)
        {
            byte[] nibblePath = Nibbles.BytesToNibbleBytes(kvp.Key.Bytes);
            SparseSubtrie.UpdateResult result = _subtrie.UpdateSingleLeaf(
                nibblePath, kvp.Value, out TreePath proofTarget);

            if (result == SparseSubtrie.UpdateResult.NeedsProof)
                proofRequired?.Invoke(kvp.Key, 0);
        }
    }

    /// <summary>
    /// Computes the state root hash via incremental bottom-up hashing.
    /// The root is always keccak-hashed regardless of RLP size.
    /// </summary>
    public Hash256 ComputeRoot()
    {
        RlpNode rootRlp = _subtrie.UpdateCachedRlp();

        if (rootRlp.IsNull || rootRlp.Length == 0 ||
            (rootRlp.Length == 1 && rootRlp.AsSpan()[0] == 0x80))
            return Keccak.EmptyTreeHash;

        return Keccak.Compute(rootRlp.AsSpan());
    }

    public void WipeStorage() => _subtrie.Wipe();

    public void Clear()
    {
        _subtrie.Wipe();
        IsRevealed = false;
    }

    /// <summary>Exposes the internal subtrie for testing.</summary>
    internal SparseSubtrie Subtrie => _subtrie;

    public void Dispose() => _subtrie.Dispose();
}
