// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

/// <summary>
/// Computes a Patricia trie's root hash by hashing dirty nodes one tree level at a time, batching every node at a
/// level into a single <see cref="IKeccakBatchHasher.HashBatch"/> call instead of the recursive per-node hashing that
/// <see cref="PatriciaTree.UpdateRootHash"/> performs.
/// </summary>
/// <remarks>
/// Produces the identical root to <see cref="PatriciaTree.UpdateRootHash"/>; it only reorders the work so the whole
/// dirty-trie hashing load exists as wide, independent per-level batches (the shape SIMD/GPU keccak backends need).
/// <para>
/// This mutates each visited node's <c>Keccak</c> and <c>FullRlp</c>, so it MUST run only on cloned, read-only nodes
/// (Lane B's <c>CreateReadOnlyTrieStore</c> clones), never on nodes shared with the live processing tree.
/// </para>
/// </remarks>
public static class BatchedTrieCommitter
{
    /// <summary>Computes and publishes <paramref name="tree"/>'s root hash using level-ordered batch hashing.</summary>
    /// <param name="tree">The trie whose dirty <c>RootRef</c> subtree is hashed; its root hash is updated in place.</param>
    /// <param name="hasher">Backend that hashes each level's nodes as one batch.</param>
    public static void UpdateRootHashBatched(PatriciaTree tree, IKeccakBatchHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(hasher);

        TrieNode? root = tree.RootRef;
        if (root is null || !root.IsDirty)
        {
            // Empty tree or a root that already carries its key: nothing to batch.
            tree.UpdateRootHash();
            return;
        }

        // ---- COLLECT: iterative post-order DFS over DIRTY nodes only, bucketed by node depth. ----
        List<List<TrieNode>> byDepth = [];
        CollectDirtyByDepth(root, byDepth);

        // ---- HASH: strict bottom-up level barriers. ----
        for (int depth = byDepth.Count - 1; depth >= 0; depth--)
        {
            HashLevel(byDepth[depth], depth == 0, hasher);
        }

        // Publish the root's freshly computed key without rebuilding RootRef (mirror of UpdateRootHash's SetRootHash).
        tree.SetRootHash(root.Keccak, resetObjects: false);
    }

    /// <summary>
    /// Computes and publishes the root hash of several MUTUALLY INDEPENDENT tries with a single merged wave: at each
    /// wave step every tree contributes its own next-deepest unprocessed level, and all those nodes are hashed in ONE
    /// <see cref="IKeccakBatchHasher.HashBatch"/> call, repeating until every tree is done.
    /// </summary>
    /// <param name="trees">The tries to hash; each must be a cloned/read-only tree (see the type remarks). May be empty.</param>
    /// <param name="hasher">Backend that hashes each wave step's concatenated nodes as one batch.</param>
    /// <remarks>
    /// Storage tries are independent, so a level barrier is only needed PER TREE, not across trees. The wave step index
    /// is per-tree-relative - tree <c>t</c> contributes its <c>s</c>-th-deepest level at wave step <c>s</c> - so a tree's
    /// shallower level is never encoded before its deeper level completes (bottom-up ordering preserved), while cross-tree
    /// alignment is arbitrary and safe. The first steps are the widest (all leaf levels together), which is the batch width
    /// SIMD/GPU backends need. Single tree yields the identical roots to <see cref="UpdateRootHashBatched"/>; empty list is
    /// a no-op; clean/no-dirty-root trees are hashed by the recursive fallback (no wave work), the root-always-hashed rule
    /// applies per tree.
    /// </remarks>
    public static void UpdateRootHashesBatched(IReadOnlyList<PatriciaTree> trees, IKeccakBatchHasher hasher) =>
        UpdateRootHashesBatched(trees, hasher, waveStats: null);

    /// <summary>Observability seam for the merged wave: records the batch width (message count) of each wave step in order.</summary>
    internal delegate void WaveStepObserver(int stepIndex, int batchWidth);

    /// <inheritdoc cref="UpdateRootHashesBatched(IReadOnlyList{PatriciaTree}, IKeccakBatchHasher)"/>
    /// <param name="waveStats">Optional per-wave-step batch-width sink (adoption-evidence seam); null in production.</param>
    internal static void UpdateRootHashesBatched(IReadOnlyList<PatriciaTree> trees, IKeccakBatchHasher hasher, WaveStepObserver? waveStats)
    {
        ArgumentNullException.ThrowIfNull(trees);
        ArgumentNullException.ThrowIfNull(hasher);

        int treeCount = trees.Count;
        if (treeCount == 0) return;

        // Per tree: its dirty nodes bucketed by depth, or null when the tree has no dirty root (recursive fallback below).
        List<List<TrieNode>>?[] byDepthPerTree = new List<List<TrieNode>>?[treeCount];
        int maxLevels = 0;
        for (int t = 0; t < treeCount; t++)
        {
            TrieNode? root = trees[t].RootRef;
            if (root is null || !root.IsDirty)
            {
                // Empty tree or a root that already carries its key: no wave work; publish via the recursive path.
                trees[t].UpdateRootHash();
                continue;
            }

            List<List<TrieNode>> byDepth = [];
            CollectDirtyByDepth(root, byDepth);
            byDepthPerTree[t] = byDepth;
            if (byDepth.Count > maxLevels) maxLevels = byDepth.Count;
        }

        if (maxLevels == 0) return; // every tree was clean/empty

        // Merged wave: step s takes each tree's s-th-deepest level (index Count-1-s). Encode all participating levels
        // (parallel across trees), then hash their to-hash nodes in ONE batch. A tree's shallower level is only reached
        // at a later step than its deeper one, so per-tree bottom-up ordering holds; cross-tree alignment is arbitrary.
        for (int step = 0; step < maxLevels; step++)
        {
            HashWaveStep(byDepthPerTree, step, hasher, waveStats);
        }

        for (int t = 0; t < treeCount; t++)
        {
            List<List<TrieNode>>? byDepth = byDepthPerTree[t];
            if (byDepth is null) continue; // clean/empty tree already published
            TrieNode root = byDepth[0][0]; // depth-0 node is the root; always hashed
            trees[t].SetRootHash(root.Keccak, resetObjects: false);
        }
    }

    /// <summary>Encodes and hashes one merged wave step: each tree's <paramref name="step"/>-th-deepest level in one batch.</summary>
    private static void HashWaveStep(
        List<List<TrieNode>>?[] byDepthPerTree,
        int step,
        IKeccakBatchHasher hasher,
        WaveStepObserver? waveStats)
    {
        // Resolve which (tree, level) pairs participate at this step and whether each is that tree's root level (d == 0).
        int treeCount = byDepthPerTree.Length;
        List<(List<TrieNode> nodes, bool isRootLevel)> levels = [];
        for (int t = 0; t < treeCount; t++)
        {
            List<List<TrieNode>>? byDepth = byDepthPerTree[t];
            if (byDepth is null) continue;
            int levelIndex = byDepth.Count - 1 - step;
            if (levelIndex < 0) continue; // this tree is shallower than the wave; it finished at an earlier step
            levels.Add((byDepth[levelIndex], levelIndex == 0));
        }

        if (levels.Count == 0) return;

        // Pass 1: encode every node's RLP into its FullRlp. Parallel across levels (each level's nodes are an independent
        // tree slice); worker t touches only its own level, so no cross-worker node is shared. Sequential for a single
        // level to avoid the parallel-region overhead when the wave has narrowed to one tree.
        if (levels.Count == 1)
        {
            EncodeLevel(levels[0].nodes);
        }
        else
        {
            ParallelUnbalancedWork.For(
                0,
                levels.Count,
                RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                w => EncodeLevel(levels[w].nodes));
        }

        HashEncodedLevels(levels, hasher, waveStats, step);
    }

    private static void CollectDirtyByDepth(TrieNode root, List<List<TrieNode>> byDepth)
    {
        Stack<(TrieNode node, int depth)> stack = new();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (TrieNode node, int depth) = stack.Pop();

            while (byDepth.Count <= depth) byDepth.Add([]);
            byDepth[depth].Add(node);

            // Branch: 16 child slots; extension: 1 child; leaf: none. TryGetDirtyChild is the ONLY safe
            // descent primitive - GetChild/GetChildWithChildPath mutate the tree via UnresolveChild.
            int childCount = node.IsBranch ? TrieNode.BranchesCount : node.IsExtension ? 1 : 0;
            for (int i = 0; i < childCount; i++)
            {
                if (node.TryGetDirtyChild(i, out TrieNode? child))
                {
                    stack.Push((child, depth + 1));
                }
                // Hash256 refs / clean nodes / null slots are wave-DAG leaves: their key already exists, no work.
            }
        }
    }

    private static void HashLevel(List<TrieNode> nodes, bool isRootLevel, IKeccakBatchHasher hasher)
    {
        EncodeLevel(nodes);
        HashEncodedLevels([(nodes, isRootLevel)], hasher, waveStats: null, stepIndex: 0);
    }

    /// <summary>Pass 1: encodes every node at a level into its own <c>FullRlp</c> from its (already-processed) children.</summary>
    /// <remarks>Children below this level are done - dirty ones carry a Keccak (>=32B) or an inline FullRlp (<32B); non-dirty ones already carried theirs.</remarks>
    private static void EncodeLevel(List<TrieNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            nodes[i].WriteRlp(FlatEncode(nodes[i]));
        }
    }

    /// <summary>
    /// Pass 2: concatenates the to-hash nodes of one or more already-encoded levels into ONE batch, hashes it, and
    /// writes each node's <c>Keccak</c> back. A node needs a key when its RLP is >= 32 bytes or it is a tree's root level.
    /// </summary>
    private static void HashEncodedLevels(
        List<(List<TrieNode> nodes, bool isRootLevel)> levels,
        IKeccakBatchHasher hasher,
        WaveStepObserver? waveStats,
        int stepIndex)
    {
        int toHashCount = 0;
        int flatLength = 0;
        for (int l = 0; l < levels.Count; l++)
        {
            (List<TrieNode> nodes, bool isRootLevel) = levels[l];
            for (int i = 0; i < nodes.Count; i++)
            {
                int rlpLength = nodes[i].FullRlp.Length;
                if (rlpLength >= 32 || isRootLevel)
                {
                    toHashCount++;
                    flatLength += rlpLength;
                }
                // else: FullRlp < 32 and not root -> Keccak stays null; parents splice its FullRlp inline.
            }
        }

        waveStats?.Invoke(stepIndex, toHashCount);
        if (toHashCount == 0) return;

        // Rents inside the try so a failed later rent still returns the earlier ones via the null-guarded finally.
        byte[]? flat = null;
        int[]? offsets = null;
        TrieNode[]? toHash = null;
        ValueHash256[]? outputs = null;
        try
        {
            flat = ArrayPool<byte>.Shared.Rent(flatLength);
            offsets = ArrayPool<int>.Shared.Rent(toHashCount);
            toHash = ArrayPool<TrieNode>.Shared.Rent(toHashCount);
            outputs = ArrayPool<ValueHash256>.Shared.Rent(toHashCount);

            Span<byte> flatSpan = flat.AsSpan(0, flatLength);
            int pos = 0;
            int j = 0;
            for (int l = 0; l < levels.Count; l++)
            {
                (List<TrieNode> nodes, bool isRootLevel) = levels[l];
                for (int i = 0; i < nodes.Count; i++)
                {
                    TrieNode node = nodes[i];
                    ReadOnlySpan<byte> rlp = node.FullRlp.AsSpan();
                    if (rlp.Length >= 32 || isRootLevel)
                    {
                        rlp.CopyTo(flatSpan.Slice(pos, rlp.Length));
                        pos += rlp.Length;
                        offsets[j] = pos;
                        toHash[j] = node;
                        j++;
                    }
                }
            }

            hasher.HashBatch(flatSpan, offsets.AsSpan(0, toHashCount), outputs.AsSpan(0, toHashCount));

            for (int k = 0; k < toHashCount; k++)
            {
                toHash[k].Keccak = new Hash256(outputs[k]);
            }
        }
        finally
        {
            if (flat is not null) ArrayPool<byte>.Shared.Return(flat);
            if (offsets is not null) ArrayPool<int>.Shared.Return(offsets);
            if (toHash is not null) ArrayPool<TrieNode>.Shared.Return(toHash, clearArray: true);
            if (outputs is not null) ArrayPool<ValueHash256>.Shared.Return(outputs);
        }
    }

    /// <summary>
    /// Encodes one node's RLP from its children, which must already carry a <c>Keccak</c> or <c>FullRlp</c> (they were
    /// processed at a deeper level). Never resolves or recurses.
    /// </summary>
    private static CappedArray<byte> FlatEncode(TrieNode node) => node.NodeType switch
    {
        NodeType.Leaf => EncodeLeaf(node),
        NodeType.Extension => EncodeExtension(node),
        NodeType.Branch => EncodeBranch(node),
        _ => ThrowUnhandled(node),
    };

    private static CappedArray<byte> EncodeLeaf(TrieNode node)
    {
        byte[] hexPrefix = node.Key!;
        int hexLength = HexPrefix.ByteLength(hexPrefix);
        Span<byte> keyBytes = hexLength <= 128 ? stackalloc byte[hexLength] : new byte[hexLength];
        HexPrefix.CopyToSpan(hexPrefix, isLeaf: true, keyBytes);

        ReadOnlySpan<byte> value = node.Value.AsSpan();
        int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(value);
        byte[] dest = new byte[Rlp.LengthOfSequence(contentLength)];
        int pos = Rlp.StartSequence(dest, 0, contentLength);
        pos = Rlp.Encode(dest, pos, keyBytes);
        Rlp.Encode(dest, pos, value);
        return new CappedArray<byte>(dest);
    }

    private static CappedArray<byte> EncodeExtension(TrieNode node)
    {
        byte[] hexPrefix = node.Key!;
        int hexLength = HexPrefix.ByteLength(hexPrefix);
        Span<byte> keyBytes = hexLength <= 128 ? stackalloc byte[hexLength] : new byte[hexLength];
        HexPrefix.CopyToSpan(hexPrefix, isLeaf: false, keyBytes);

        int childRefLength = ChildRefLength(node, 0);
        int contentLength = Rlp.LengthOf(keyBytes) + childRefLength;
        byte[] dest = new byte[Rlp.LengthOfSequence(contentLength)];
        int pos = Rlp.StartSequence(dest, 0, contentLength);
        pos = Rlp.Encode(dest, pos, keyBytes);
        WriteChildRef(dest, pos, node, 0);
        return new CappedArray<byte>(dest);
    }

    private static CappedArray<byte> EncodeBranch(TrieNode node)
    {
        // Hardcoded 0x80 value byte assumes fixed-width-key tries (state/storage), which never store a branch value.
        Debug.Assert(node.Value.Length == 0, "Batched committer only supports valueless branches (fixed-width-key tries).");

        const int valueRlpLength = 1; // trailing empty-string value byte (0x80) for state/storage branches
        int contentLength = valueRlpLength;
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            contentLength += ChildRefLength(node, i);
        }

        byte[] dest = new byte[Rlp.LengthOfSequence(contentLength)];
        int pos = Rlp.StartSequence(dest, 0, contentLength);
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            pos = WriteChildRef(dest, pos, node, i);
        }

        dest[pos] = 128; // empty value
        return new CappedArray<byte>(dest);
    }

    /// <summary>RLP length child slot <paramref name="i"/> contributes to its parent <paramref name="node"/>.</summary>
    private static int ChildRefLength(TrieNode node, int i)
    {
        object? childSlot = node.NodeData![i];
        if (childSlot is TrieNode childNode)
        {
            return childNode.Keccak is not null ? Rlp.LengthOfKeccakRlp : childNode.FullRlp.Length;
        }

        if (childSlot is Hash256)
        {
            return Rlp.LengthOfKeccakRlp;
        }

        // Genuinely-null slot on a node that retained its pre-mutation RLP: the child's ref lives in that RLP and is
        // spliced out verbatim (mirrors TrieNodeDecoder's null-slot branch). The _nullNode sentinel is not reference-null
        // and falls through to the 0x80 absent-child byte.
        if (childSlot is null && node.HasRlp)
        {
            return node.GetChildRefFromOwnRlp(i).Length;
        }

        return 1; // absent child -> RLP empty string (0x80)
    }

    /// <summary>Writes child slot <paramref name="i"/>'s RLP reference at <paramref name="pos"/>; returns the new position.</summary>
    private static int WriteChildRef(byte[] dest, int pos, TrieNode node, int i)
    {
        object? childSlot = node.NodeData![i];
        if (childSlot is TrieNode childNode)
        {
            Hash256? keccak = childNode.Keccak;
            if (keccak is not null)
            {
                return Rlp.Encode(dest, pos, keccak);
            }

            // <32-byte child: splice its raw RLP inline (it was encoded at a deeper level).
            ReadOnlySpan<byte> childRlp = childNode.FullRlp.AsSpan();
            Debug.Assert(childRlp.Length > 0, "Inline child FullRlp not set - child was not processed at a deeper level.");
            childRlp.CopyTo(dest.AsSpan(pos, childRlp.Length));
            return pos + childRlp.Length;
        }

        if (childSlot is Hash256 hash)
        {
            return Rlp.Encode(dest, pos, hash);
        }

        // Genuinely-null slot with retained RLP: copy the child's ref bytes from this node's own RLP (see ChildRefLength).
        if (childSlot is null && node.HasRlp)
        {
            ReadOnlySpan<byte> childRef = node.GetChildRefFromOwnRlp(i);
            childRef.CopyTo(dest.AsSpan(pos, childRef.Length));
            return pos + childRef.Length;
        }

        dest[pos] = 128; // absent child
        return pos + 1;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static CappedArray<byte> ThrowUnhandled(TrieNode node) =>
        throw new TrieException($"Cannot flat-encode trie node of type {node.NodeType}");
}
