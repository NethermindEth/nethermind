// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    /// Reveals proof nodes into the sparse trie. Nodes are processed in path order so
    /// consecutive proofs can resume from their nearest common revealed ancestor. When
    /// the trie is empty, the batch must contain the root proof.
    /// </summary>
    public void RevealNodes(IReadOnlyList<ProofNode> proofNodes)
    {
        _subtrie.ReserveForReveal(proofNodes.Count);
        Span<RevealCursorEntry> cursor = stackalloc RevealCursorEntry[65];
        int cursorLength = 0;

        bool sorted = true;
        for (int i = 1; i < proofNodes.Count; i++)
        {
            TreePath previous = proofNodes[i - 1].Path;
            TreePath current = proofNodes[i].Path;
            if (previous.CompareTo(in current) > 0)
            {
                sorted = false;
                break;
            }
        }

        if (sorted)
        {
            for (int i = 0; i < proofNodes.Count; i++)
                RevealNode(proofNodes[i], cursor, ref cursorLength);
        }
        else
        {
            ProofNode[] ordered = ArrayPool<ProofNode>.Shared.Rent(proofNodes.Count);
            try
            {
                for (int i = 0; i < proofNodes.Count; i++)
                    ordered[i] = proofNodes[i];
                Array.Sort(ordered, 0, proofNodes.Count, ProofNodePathComparer.Instance);
                for (int i = 0; i < proofNodes.Count; i++)
                    RevealNode(ordered[i], cursor, ref cursorLength);
            }
            finally
            {
                Array.Clear(ordered, 0, proofNodes.Count);
                ArrayPool<ProofNode>.Shared.Return(ordered);
            }
        }

        IsRevealed = true;
    }

    private void RevealNode(
        ProofNode proofNode,
        Span<RevealCursorEntry> cursor,
        ref int cursorLength)
    {
        if (_subtrie.Root == -1 || _subtrie.NodeAt(_subtrie.Root).IsEmpty())
        {
            if (_subtrie.Root >= 0)
                _subtrie.FreeNode(_subtrie.Root);
            _subtrie.Root = CreateNodeFromProof(_subtrie, proofNode);
            cursor[0] = new RevealCursorEntry(_subtrie.Root, TreePath.Empty);
            cursorLength = 1;
            return;
        }

        TreePath targetPath = proofNode.Path;
        if (targetPath.Length == 0)
            return;

        if (cursorLength == 0)
        {
            cursor[0] = new RevealCursorEntry(_subtrie.Root, TreePath.Empty);
            cursorLength = 1;
        }

        while (cursorLength > 1)
        {
            TreePath ancestorPath = cursor[cursorLength - 1].Path;
            if (targetPath.Length >= ancestorPath.Length && targetPath.StartsWith(ancestorPath))
                break;
            cursorLength--;
        }

        int currentIdx = cursor[cursorLength - 1].ArenaIndex;
        TreePath currentPath = cursor[cursorLength - 1].Path;
        if (currentPath.Length == targetPath.Length)
            return;

        while (currentPath.Length < targetPath.Length)
        {
            SparseTrieNode current = _subtrie.NodeAt(currentIdx);
            if (!current.IsBranch())
                return;

            byte[] shortKey = current.ShortKey ?? [];
            if (!MatchesPath(targetPath, currentPath.Length, shortKey))
                return;

            int branchPathLength = currentPath.Length + shortKey.Length;
            if (branchPathLength == targetPath.Length)
            {
                if (current.HasShortKey() && current.ChildCount() == 0)
                    MergeChildIntoBranchWithExtension(currentIdx, proofNode);
                return;
            }

            int nibble = targetPath[branchPathLength];
            if (!current.StateMask.IsBitSet(nibble))
                return;

            int denseIdx = current.DenseChildIndex(nibble);
            SparseChildEntry childEntry = _subtrie.ChildAt(denseIdx);
            TreePath childPath = currentPath.Append(shortKey).Append(nibble);

            if (childEntry.IsBlinded)
            {
                if (childPath.Length != targetPath.Length)
                    return;

                int newNodeIdx = CreateNodeFromProof(_subtrie, proofNode);
                denseIdx = _subtrie.NodeAt(currentIdx).DenseChildIndex(nibble);
                _subtrie.ChildAt(denseIdx) = SparseChildEntry.Revealed(newNodeIdx);
                _subtrie.NodeAt(currentIdx).BlindedMask =
                    _subtrie.NodeAt(currentIdx).BlindedMask.ClearBit(nibble);
                cursor[cursorLength++] = new RevealCursorEntry(newNodeIdx, childPath);
                return;
            }

            currentIdx = childEntry.ArenaIndex;
            currentPath = childPath;
            cursor[cursorLength++] = new RevealCursorEntry(currentIdx, currentPath);
        }
    }

    private static bool MatchesPath(in TreePath path, int offset, ReadOnlySpan<byte> segment)
    {
        if (offset + segment.Length > path.Length)
            return false;
        for (int i = 0; i < segment.Length; i++)
        {
            if (path[offset + i] != segment[i])
                return false;
        }

        return true;
    }

    private readonly record struct RevealCursorEntry(int ArenaIndex, TreePath Path);

    private sealed class ProofNodePathComparer : IComparer<ProofNode>
    {
        public static ProofNodePathComparer Instance { get; } = new();

        public int Compare(ProofNode? x, ProofNode? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            TreePath xPath = x.Path;
            TreePath yPath = y.Path;
            return xPath.CompareTo(in yPath);
        }
    }

    /// <summary>
    /// Merges a child proof node into an existing extension-only branch (created from an Extension
    /// proof with empty StateMask). The child can be a Branch (typical), Leaf, or Extension —
    /// Patricia's MaybeCombineNode normally folds extension+leaf/extension+extension into a single
    /// node, but a proof walker can encounter these shapes when the extension's child was just
    /// revealed at a different point in the cycle and needs to be merged in place.
    /// </summary>
    private void MergeChildIntoBranchWithExtension(int extensionIdx, ProofNode childProof)
    {
        ref SparseTrieNode ext = ref _subtrie.NodeAt(extensionIdx);
        byte[]? shortKey = ext.ShortKey ?? [];
        // The pre-merge extension-only node owns a single-slot children slice holding the
        // blinded child. Once we transition it to a fully-revealed branch (or replace the
        // node with a leaf below), nothing references that slot, so return it to the size-1
        // free list. Captured BEFORE we mutate ext, since AllocChildren may pop it back.
        int oldChildrenStart = ext.ChildrenStart;

        if (childProof.Kind == ProofNodeKind.Branch)
        {
            RlpNode extensionCachedRlp = ext.CachedRlp;
            int childCount = childProof.ChildMask.CountBits();
            if (oldChildrenStart >= 0) _subtrie.FreeChildren(oldChildrenStart, 1);
            int childStart = _subtrie.AllocChildren(childCount);
            TrieMask mask = childProof.ChildMask;
            int dense = 0;
            for (int n = 0; n < 16; n++)
            {
                if (!mask.IsBitSet(n)) continue;
                RlpNode childRlp = childProof.ChildRlps is not null && n < childProof.ChildRlps.Length
                    ? childProof.ChildRlps[n]
                    : default;
                _subtrie.ChildAt(childStart + dense) = SparseChildEntry.Blinded(childRlp);
                dense++;
            }

            ext = ref _subtrie.NodeAt(extensionIdx);
            ext.StateMask = mask;
            ext.BlindedMask = mask;
            ext.ChildrenStart = childStart;
            ext.ShortKey = shortKey;
            ext.CachedRlp = extensionCachedRlp;
            ext.InnerBranchRlp = childProof.RawRlp;
            ext.State = SparseNodeState.Cached;
            return;
        }

        if (childProof.Kind == ProofNodeKind.Leaf)
        {
            // Extension + Leaf collapses to a single Leaf with combined key.
            byte[] leafKey = childProof.Key ?? [];
            byte[] combinedKey = new byte[shortKey!.Length + leafKey.Length];
            shortKey.CopyTo(combinedKey.AsSpan());
            leafKey.CopyTo(combinedKey.AsSpan(shortKey.Length));

            // The extension's 1-slot child slice is no longer referenced once we rewrite ext
            // as a leaf â€” return it before allocating the value.
            if (oldChildrenStart >= 0) _subtrie.FreeChildren(oldChildrenStart, 1);
            int valIdx = _subtrie.AllocValue(childProof.Value ?? []);
            ext = ref _subtrie.NodeAt(extensionIdx);
            ext.Kind = SparseNodeKind.Leaf;
            ext.State = SparseNodeState.Dirty; // recompute CachedRlp on next encode
            ext.StateMask = TrieMask.Empty;
            ext.BlindedMask = TrieMask.Empty;
            ext.ShortKey = combinedKey;
            ext.ValueIndex = valIdx;
            ext.ChildrenStart = -1;
            ext.CachedRlp = default;
            ext.FullRlp = null;
            ext.InnerBranchRlp = null;
            _subtrie.NumLeaves++;
            _subtrie.NumDirtyLeaves++;
            return;
        }

        if (childProof.Kind == ProofNodeKind.Extension)
        {
            // Extension + Extension collapses to a single Extension with concatenated keys.
            // The deeper extension's child stays blinded at slot 0; we extend the outer key.
            byte[] innerKey = childProof.Key ?? [];
            byte[] combinedKey = new byte[shortKey!.Length + innerKey.Length];
            shortKey.CopyTo(combinedKey.AsSpan());
            innerKey.CopyTo(combinedKey.AsSpan(shortKey.Length));

            RlpNode innerChildRef = childProof.ChildRlps is { Length: > 0 } ? childProof.ChildRlps[0] : default;
            // Old 1-slot is being replaced by another 1-slot â€” free + alloc will normally
            // pop the same slot back, so this is allocation-free in steady state.
            if (oldChildrenStart >= 0) _subtrie.FreeChildren(oldChildrenStart, 1);
            int childStart = _subtrie.AllocChildren(1);
            _subtrie.ChildAt(childStart) = SparseChildEntry.Blinded(innerChildRef);

            ext = ref _subtrie.NodeAt(extensionIdx);
            ext.StateMask = TrieMask.Empty;
            ext.BlindedMask = TrieMask.Empty;
            ext.ChildrenStart = childStart;
            ext.ShortKey = combinedKey;
            ext.State = SparseNodeState.Dirty; // recompute via EncodeExtensionOnly
            ext.CachedRlp = default;
            ext.FullRlp = null;
            ext.InnerBranchRlp = null;
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
                    // Extensions are represented as branches with a ShortKey and an empty
                    // StateMask. The extension's child reference (a hash or inline RLP) is
                    // stored at ChildrenStart[0] as a blinded entry. Encoding detects this
                    // shape (HasShortKey + empty StateMask) and emits a 2-item extension RLP,
                    // NOT a 17-item branch RLP wrapped in an extension. Previously the child
                    // ref was dropped when ChildNibble was -1, which made CollapseBranch
                    // mutate ShortKey and re-encode as `extension(key, empty-17-element-branch)`
                    // producing a non-canonical hash.
                    RlpNode childRlp = proofNode.ChildRlps is not null && proofNode.ChildRlps.Length > 0
                        ? proofNode.ChildRlps[0]
                        : default;
                    int childStart = subtrie.AllocChildren(1);
                    subtrie.ChildAt(childStart) = SparseChildEntry.Blinded(childRlp);

                    SparseTrieNode node = SparseTrieNode.CreateBranchWithExtension(
                        proofNode.Key ?? [], TrieMask.Empty, childStart);
                    node.BlindedMask = TrieMask.Empty;
                    node.CachedRlp = RlpNode.FromRlp(proofNode.RawRlp ?? []);
                    node.State = SparseNodeState.Cached;
                    return subtrie.AllocNode(node);
                }

            default:
                return subtrie.AllocNode(SparseTrieNode.CreateEmpty());
        }
    }

    /// <summary>
    /// Applies leaf updates to the trie. Updates that hit blinded nodes invoke the callback
    /// with (target, minLen) where minLen is the depth at which the blind was hit. The retry
    /// proof reader can then skip nodes ABOVE minLen for that target (they're already revealed
    /// in this sparse trie). This mirrors Reth's ProofV2Target.with_min_len() optimization.
    /// </summary>
    /// <remarks>
    /// Keys are <see cref="ValueHash256"/> â€” the value-type hash form â€” so callers can
    /// produce keys via <c>ValueKeccak.Compute</c> without per-slot <see cref="Hash256"/>
    /// (class) allocations. This is the dominant per-block alloc source under sparse mode.
    /// </remarks>
    public void UpdateLeaves(
        Dictionary<ValueHash256, LeafUpdate> updates,
        Action<ValueHash256, byte>? proofRequired)
    {
        int count = updates.Count;
        if (count == 0) return;

        ValueHash256[] keys = ArrayPool<ValueHash256>.Shared.Rent(count);
        try
        {
            int i = 0;
            foreach (ValueHash256 k in updates.Keys) keys[i++] = k;
            UpdateLeavesSorted(updates, keys.AsSpan(0, count), proofRequired);
        }
        finally
        {
            ArrayPool<ValueHash256>.Shared.Return(keys);
        }
    }

    /// <summary>
    /// Applies only the given subset of keys from <paramref name="updates"/>. Used by the
    /// reveal-update-retry loop so each retry re-processes ONLY the keys that hit a blinded node
    /// last time (the misses), instead of re-sorting and re-walking the entire change set every
    /// iteration. Mirrors Reth's "drain applied, reinsert only misses" â€” on a block with N
    /// changes and a couple of blinded boundaries, this turns O(retries Ã— N) leaf walks into
    /// O(N + misses), which is the dominant avoidable cost once proofs are warm.
    /// </summary>
    /// <param name="keysToApply">
    /// The subset to apply this pass. Sorted in place (ascending = nibble-path order) so the
    /// caller's buffer must be writable. Misses are reported via <paramref name="proofRequired"/>.
    /// </param>
    public void UpdateLeavesSubset(
        Dictionary<ValueHash256, LeafUpdate> updates,
        Span<ValueHash256> keysToApply,
        Action<ValueHash256, byte>? proofRequired)
        => UpdateLeavesSorted(updates, keysToApply, proofRequired);

    private void UpdateLeavesSorted(
        Dictionary<ValueHash256, LeafUpdate> updates,
        Span<ValueHash256> keys,
        Action<ValueHash256, byte>? proofRequired)
    {
        // Apply updates in sorted key order rather than Dictionary hash order. A hashed key's
        // nibble path is just its bytes expanded, so ascending ValueHash256 order == ascending
        // nibble-path (lexicographic) order. Walking the arena in path order means consecutive
        // updates share long prefixes â€” the descent re-touches the same upper branches back to
        // back (warm in cache) instead of jumping randomly across the trie, and dirty
        // propagation marks each subtree once. The sort is O(n log n) over 32-byte structs;
        // cheap next to the per-leaf trie descent it accelerates.
        keys.Sort();

        // The hash key is always 32 bytes â†’ 64 nibbles. Reuse one stack buffer across the
        // loop so we don't allocate a 64-byte managed array per leaf.
        Span<byte> nibblePath = stackalloc byte[64];
        for (int k = 0; k < keys.Length; k++)
        {
            ValueHash256 key = keys[k];
            LeafUpdate update = updates[key];
            Nibbles.BytesToNibbleBytes(key.Bytes, nibblePath);
            SparseSubtrie.UpdateResult result = _subtrie.UpdateSingleLeaf(
                nibblePath, update, out TreePath proofTarget);

            if (result == SparseSubtrie.UpdateResult.NeedsProof)
            {
                // depth walked through sparse trie before hitting blinded = totalNibbles - remainingPath.
                // proofTarget is the REMAINING path (including the blinded nibble), so minLen = nibbles consumed.
                int minLen = nibblePath.Length - proofTarget.Length;
                proofRequired?.Invoke(key, (byte)Math.Min(minLen, byte.MaxValue));
            }
        }
    }

    /// <summary>
    /// Computes the state root hash via incremental bottom-up hashing.
    /// The root is always keccak-hashed regardless of RLP size.
    /// </summary>
    /// <param name="allowParallel">
    /// When false, hashing is fully sequential. Pass false for per-contract storage tries that
    /// are themselves being computed in parallel by
    /// <c>PersistentStorageProvider.UpdateRootHashesMultiThread</c>, otherwise the inner
    /// <c>Parallel.For</c> nests inside the outer parallel context and oversubscribes the pool.
    /// </param>
    public Hash256 ComputeRoot(bool allowParallel = true)
    {
        // UpdateCachedRlp returns CachedRlp (child-ref form) which for the root is either
        // a 32-byte hash (already keccaked) or inline RLP < 32 bytes.
        // For inline root, we keccak the inline bytes. For hash root, return the hash directly.
        RlpNode rootRlp = _subtrie.UpdateCachedRlp(allowParallel);

        if (rootRlp.IsNull || rootRlp.Length == 0 ||
            (rootRlp.Length == 1 && rootRlp.AsSpan()[0] == 0x80))
            return Keccak.EmptyTreeHash;

        if (rootRlp.IsHash())
            return rootRlp.AsHash();

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

    /// <summary>Cheap proxy for this trie's retained memory footprint (arena high-water mark).
    /// Used by cross-block cache size reporting.</summary>
    public int ArenaHighWater => _subtrie.ArenaHighWater;

    public void Dispose() => _subtrie.Dispose();
}
