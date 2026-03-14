// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Traverses a Merkle Patricia Trie at the RLP level without creating <see cref="TrieNode"/> objects.
/// This eliminates GC pressure during warmup by working directly with raw RLP bytes.
/// </summary>
internal static class RlpTrieTraversal
{
    private const int MaxKeyStackAlloc = 64; // 32 bytes * 2 nibbles = 64 nibbles

    /// <summary>
    /// Traverses the trie along the path described by <paramref name="rawKey"/>, loading and caching
    /// each node's RLP via <paramref name="rlpLoader"/>. No <see cref="TrieNode"/> objects are created.
    /// </summary>
    /// <param name="rlpLoader">
    /// Loads RLP bytes for a node identified by its path and hash.
    /// Returns <c>null</c> if the node is not available (cache miss + no disk fallback).
    /// </param>
    /// <param name="rootHash">The root hash of the trie to traverse.</param>
    /// <param name="rawKey">The raw key bytes to look up (not nibble-encoded).</param>
    public static void WarmUpPath(
        Func<TreePath, Hash256, byte[]?> rlpLoader,
        Hash256 rootHash,
        ReadOnlySpan<byte> rawKey)
    {
        int nibblesCount = 2 * rawKey.Length;
        byte[]? rentedArray = null;
        Span<byte> nibbles = nibblesCount <= MaxKeyStackAlloc
            ? stackalloc byte[MaxKeyStackAlloc]
            : (rentedArray = ArrayPool<byte>.Shared.Rent(nibblesCount));

        try
        {
            nibbles = nibbles[..nibblesCount];
            Nibbles.BytesToNibbleBytes(rawKey, nibbles);

            TreePath path = TreePath.Empty;
            Hash256 currentHash = rootHash;

            WarmUpHashedNode(rlpLoader, ref path, currentHash, nibbles);
        }
        finally
        {
            if (rentedArray is not null) ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    private static void WarmUpHashedNode(
        Func<TreePath, Hash256, byte[]?> rlpLoader,
        ref TreePath path,
        Hash256 hash,
        Span<byte> remainingNibbles)
    {
        // Iterative loop for hash-referenced nodes; recurse only for inline nodes.
        while (true)
        {
            byte[]? rlp = rlpLoader(path, hash);
            if (rlp is null) return;

            bool continueLoop = TraverseNode(rlpLoader, ref path, rlp, remainingNibbles, out Hash256? nextHash, out int nibblesConsumed);
            if (!continueLoop || nextHash is null) return;

            remainingNibbles = remainingNibbles[nibblesConsumed..];
            hash = nextHash;
        }
    }

    /// <summary>
    /// Parses one node from <paramref name="rlp"/> and determines the next hash and nibbles consumed.
    /// Returns false when traversal should stop (leaf reached, missing child, key mismatch).
    /// </summary>
    private static bool TraverseNode(
        Func<TreePath, Hash256, byte[]?> rlpLoader,
        ref TreePath path,
        byte[] rlp,
        Span<byte> remainingNibbles,
        out Hash256? nextHash,
        out int nibblesConsumed)
    {
        nextHash = null;
        nibblesConsumed = 0;

        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        int sequenceLength = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + sequenceLength;
        int itemCount = ctx.PeekNumberOfItemsRemaining(endPosition, maxSearch: 3);

        if (itemCount == 2)
        {
            return TraverseExtensionOrLeaf(rlpLoader, ref path, rlp, ref ctx, remainingNibbles, out nextHash, out nibblesConsumed);
        }

        // Branch node (17 items)
        return TraverseBranch(rlpLoader, ref path, rlp, ref ctx, remainingNibbles, out nextHash, out nibblesConsumed);
    }

    private static bool TraverseExtensionOrLeaf(
        Func<TreePath, Hash256, byte[]?> rlpLoader,
        ref TreePath path,
        byte[] parentRlp,
        ref Rlp.ValueDecoderContext ctx,
        Span<byte> remainingNibbles,
        out Hash256? nextHash,
        out int nibblesConsumed)
    {
        nextHash = null;
        nibblesConsumed = 0;

        // First item: compact-encoded path prefix
        (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
        if (contentLen == 0)
        {
            // Empty key segment — skip
            return false;
        }

        ReadOnlySpan<byte> compactKeyBytes = ctx.Read(contentLen);
        byte firstByte = compactKeyBytes[0];
        bool isLeaf = (firstByte & 0x20) != 0;

        if (isLeaf) return false; // Reached a leaf — done

        // Extension node: decode nibbles from compact encoding
        bool isOdd = (firstByte & 0x10) != 0;
        int keyNibbleCount = (compactKeyBytes.Length - 1) * 2 + (isOdd ? 1 : 0);

        // Verify the remaining key starts with the extension's path
        if (remainingNibbles.Length < keyNibbleCount) return false;

        // Check nibble match (inline to avoid allocation)
        int compactIdx = 0;
        int nibIdx = 0;
        if (isOdd)
        {
            if (remainingNibbles[nibIdx++] != (firstByte & 0x0F)) return false;
            compactIdx = 1;
        }
        else
        {
            compactIdx = 1; // skip the prefix byte
        }

        while (compactIdx < compactKeyBytes.Length)
        {
            byte b = compactKeyBytes[compactIdx++];
            if (remainingNibbles[nibIdx++] != (b >> 4)) return false;
            if (nibIdx < keyNibbleCount && remainingNibbles[nibIdx++] != (b & 0x0F)) return false;
        }

        // Advance path by the extension key nibbles
        path.AppendMut(remainingNibbles[..keyNibbleCount]);
        nibblesConsumed = keyNibbleCount;

        // Second item: child reference
        return TryReadChildRef(rlpLoader, ref path, parentRlp, ref ctx, remainingNibbles[keyNibbleCount..], out nextHash);
    }

    private static bool TraverseBranch(
        Func<TreePath, Hash256, byte[]?> rlpLoader,
        ref TreePath path,
        byte[] parentRlp,
        ref Rlp.ValueDecoderContext ctx,
        Span<byte> remainingNibbles,
        out Hash256? nextHash,
        out int nibblesConsumed)
    {
        nextHash = null;
        nibblesConsumed = 0;

        if (remainingNibbles.IsEmpty) return false;

        int nib = remainingNibbles[0];

        // Skip to the correct child slot
        for (int i = 0; i < nib; i++) ctx.SkipItem();

        path.AppendMut(nib);
        nibblesConsumed = 1;

        return TryReadChildRef(rlpLoader, ref path, parentRlp, ref ctx, remainingNibbles[1..], out nextHash);
    }

    /// <summary>
    /// Reads a child reference at the current position in <paramref name="ctx"/>.
    /// Handles empty refs (0x80), hash refs (0xA0 + 32 bytes), and inline nodes (sequence).
    /// </summary>
    private static bool TryReadChildRef(
        Func<TreePath, Hash256, byte[]?> rlpLoader,
        ref TreePath path,
        byte[] parentRlp,
        ref Rlp.ValueDecoderContext ctx,
        Span<byte> remainingNibbles,
        out Hash256? nextHash)
    {
        nextHash = null;

        if (ctx.Position >= parentRlp.Length) return false;

        byte prefix = parentRlp[ctx.Position];

        if (prefix == 0x80)
        {
            // Empty child
            return false;
        }

        if (prefix == 0xA0)
        {
            // Hash reference (0xA0 = 0x80 + 32)
            ctx.Position++;
            if (ctx.Position + 32 > parentRlp.Length) return false;

            nextHash = new Hash256(parentRlp.AsSpan(ctx.Position, 32));
            return true;
        }

        if (prefix >= 0xC0)
        {
            // Inline node — parse directly from parent's RLP at current position
            int inlineStart = ctx.Position;
            int inlineLen = ctx.PeekNextRlpLength();
            byte[] inlineRlp = parentRlp[inlineStart..(inlineStart + inlineLen)];

            // Traverse the inline node without loading from cache (it has no hash)
            TraverseNode(rlpLoader, ref path, inlineRlp, remainingNibbles, out nextHash, out _);

            // nextHash may be set if the inline node leads to a hash-referenced child
            return nextHash is not null;
        }

        // Unknown prefix — abort
        return false;
    }
}
