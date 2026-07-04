// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
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
        // Pass 1: encode every node's RLP into its FullRlp. Children below this level are done - dirty ones carry
        // a Keccak (>=32B) or an inline FullRlp (<32B); non-dirty ones already carried theirs.
        for (int i = 0; i < nodes.Count; i++)
        {
            nodes[i].WriteRlp(FlatEncode(nodes[i]));
        }

        // Pass 2: batch-hash the nodes that need a key: RLP >= 32 bytes, or the root (always hashed).
        int toHashCount = 0;
        int flatLength = 0;
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
