// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Pure RLP-based trie traversal utilities for prewarming trie paths without creating TrieNode objects.
/// </summary>
public static class RlpTrieTraversal
{
    /// <summary>
    /// RLP trie node type determined from the RLP structure.
    /// </summary>
    public enum RlpNodeType
    {
        /// <summary>Node with invalid or unrecognized structure.</summary>
        Invalid,
        /// <summary>Branch node: 17-item RLP list (16 children + value).</summary>
        Branch,
        /// <summary>Extension node: 2-item RLP list with even HP prefix.</summary>
        Extension,
        /// <summary>Leaf node: 2-item RLP list with odd HP prefix.</summary>
        Leaf
    }

    /// <summary>
    /// Determines the node type from raw RLP data.
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded trie node.</param>
    /// <returns>The node type (Branch, Extension, Leaf, or Invalid).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RlpNodeType GetNodeType(ReadOnlySpan<byte> rlp)
    {
        if (rlp.IsEmpty) return RlpNodeType.Invalid;

        Rlp.ValueDecoderContext ctx = new(rlp);

        // Read sequence length to position at first item
        try
        {
            ctx.ReadSequenceLength();
        }
        catch (RlpException)
        {
            return RlpNodeType.Invalid;
        }

        // Count items - 17 = branch, 2 = extension/leaf
        int numberOfItems = ctx.PeekNumberOfItemsRemaining(null, 3);

        if (numberOfItems > 2)
        {
            return RlpNodeType.Branch;
        }

        if (numberOfItems == 2)
        {
            // Distinguish between extension and leaf by hex prefix
            ReadOnlySpan<byte> keyBytes = ctx.DecodeByteArraySpan();
            if (keyBytes.IsEmpty)
            {
                return RlpNodeType.Invalid;
            }

            // HP encoding: first nibble indicates type
            // 0x0: extension with even length
            // 0x1: extension with odd length
            // 0x2: leaf with even length
            // 0x3: leaf with odd length
            bool isLeaf = (keyBytes[0] & 0x20) != 0;
            return isLeaf ? RlpNodeType.Leaf : RlpNodeType.Extension;
        }

        return RlpNodeType.Invalid;
    }

    /// <summary>
    /// Extracts key nibbles from a leaf or extension node's RLP data.
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded leaf or extension node.</param>
    /// <returns>Nibble array representing the key, or null if not a valid leaf/extension.</returns>
    public static byte[]? GetKey(ReadOnlySpan<byte> rlp)
    {
        if (rlp.IsEmpty) return null;

        Rlp.ValueDecoderContext ctx = new(rlp);

        try
        {
            ctx.ReadSequenceLength();
            int numberOfItems = ctx.PeekNumberOfItemsRemaining(null, 3);

            if (numberOfItems != 2)
            {
                return null; // Not a leaf or extension
            }

            ReadOnlySpan<byte> keyBytes = ctx.DecodeByteArraySpan();
            if (keyBytes.IsEmpty)
            {
                return null;
            }

            (byte[] key, bool _) = HexPrefix.FromBytes(keyBytes);
            return key;
        }
        catch (RlpException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets child hash at a specific index (0-15) from a branch node.
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded branch node.</param>
    /// <param name="index">Child index (0-15).</param>
    /// <param name="hash">Output hash if the child is a 32-byte hash reference.</param>
    /// <returns>
    /// True if a valid hash was found at the index.
    /// False if the child is empty, inline, or the RLP is invalid.
    /// </returns>
    public static bool TryGetBranchChildHash(ReadOnlySpan<byte> rlp, int index, out Hash256? hash)
    {
        hash = null;

        if (rlp.IsEmpty || index is < 0 or > 15)
        {
            return false;
        }

        Rlp.ValueDecoderContext ctx = new(rlp);

        try
        {
            ctx.ReadSequenceLength();

            // Skip to the requested index
            for (int i = 0; i < index; i++)
            {
                ctx.SkipItem();
            }

            // Check the prefix to determine type
            int prefix = ctx.PeekByte();

            // 160 (0xA0) = 128 + 32, indicating a 32-byte hash
            if (prefix == 160)
            {
                hash = ctx.DecodeKeccak();
                return hash is not null;
            }

            // Empty node: 128 (0x80) = empty byte array, or 0x00
            // Or inline node: prefix >= 192 (0xC0) = list
            return false;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets child hash at a specific index (0-15) from a branch node as a ValueHash256.
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded branch node.</param>
    /// <param name="index">Child index (0-15).</param>
    /// <param name="hash">Output hash if the child is a 32-byte hash reference.</param>
    /// <returns>
    /// True if a valid hash was found at the index.
    /// False if the child is empty, inline, or the RLP is invalid.
    /// </returns>
    public static bool TryGetBranchChildValueHash(ReadOnlySpan<byte> rlp, int index, out ValueHash256 hash)
    {
        Unsafe.SkipInit(out hash);

        if (rlp.IsEmpty || index is < 0 or > 15)
        {
            return false;
        }

        Rlp.ValueDecoderContext ctx = new(rlp);

        try
        {
            ctx.ReadSequenceLength();

            // Skip to the requested index
            for (int i = 0; i < index; i++)
            {
                ctx.SkipItem();
            }

            // Check the prefix to determine type
            int prefix = ctx.PeekByte();

            // 160 (0xA0) = 128 + 32, indicating a 32-byte hash
            if (prefix == 160)
            {
                ValueHash256? nullableHash = ctx.DecodeValueKeccak();
                if (nullableHash.HasValue)
                {
                    hash = nullableHash.Value;
                    return true;
                }
            }

            return false;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the child hash from an extension node.
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded extension node.</param>
    /// <param name="hash">Output hash if the child is a 32-byte hash reference.</param>
    /// <returns>
    /// True if a valid hash was found.
    /// False if the child is inline or the RLP is invalid.
    /// </returns>
    public static bool TryGetExtensionChildHash(ReadOnlySpan<byte> rlp, out Hash256? hash)
    {
        hash = null;

        if (rlp.IsEmpty) return false;

        Rlp.ValueDecoderContext ctx = new(rlp);

        try
        {
            ctx.ReadSequenceLength();
            int numberOfItems = ctx.PeekNumberOfItemsRemaining(null, 3);

            if (numberOfItems != 2)
            {
                return false;
            }

            // Verify it's an extension (not a leaf)
            ReadOnlySpan<byte> keyBytes = ctx.DecodeByteArraySpan();
            if (keyBytes.IsEmpty || (keyBytes[0] & 0x20) != 0)
            {
                // Empty key or is a leaf node
                return false;
            }

            // Get the child reference
            int prefix = ctx.PeekByte();

            // 160 (0xA0) = 128 + 32, indicating a 32-byte hash
            if (prefix == 160)
            {
                hash = ctx.DecodeKeccak();
                return hash is not null;
            }

            return false;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the child hash from an extension node as a ValueHash256.
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded extension node.</param>
    /// <param name="hash">Output hash if the child is a 32-byte hash reference.</param>
    /// <returns>
    /// True if a valid hash was found.
    /// False if the child is inline or the RLP is invalid.
    /// </returns>
    public static bool TryGetExtensionChildValueHash(ReadOnlySpan<byte> rlp, out ValueHash256 hash)
    {
        Unsafe.SkipInit(out hash);

        if (rlp.IsEmpty) return false;

        Rlp.ValueDecoderContext ctx = new(rlp);

        try
        {
            ctx.ReadSequenceLength();
            int numberOfItems = ctx.PeekNumberOfItemsRemaining(null, 3);

            if (numberOfItems != 2)
            {
                return false;
            }

            // Verify it's an extension (not a leaf)
            ReadOnlySpan<byte> keyBytes = ctx.DecodeByteArraySpan();
            if (keyBytes.IsEmpty || (keyBytes[0] & 0x20) != 0)
            {
                // Empty key or is a leaf node
                return false;
            }

            // Get the child reference
            int prefix = ctx.PeekByte();

            // 160 (0xA0) = 128 + 32, indicating a 32-byte hash
            if (prefix == 160)
            {
                ValueHash256? nullableHash = ctx.DecodeValueKeccak();
                if (nullableHash.HasValue)
                {
                    hash = nullableHash.Value;
                    return true;
                }
            }

            return false;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a branch child at the given index is empty (null node).
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded branch node.</param>
    /// <param name="index">Child index (0-15).</param>
    /// <returns>True if the child is empty, false otherwise.</returns>
    public static bool IsBranchChildEmpty(ReadOnlySpan<byte> rlp, int index)
    {
        if (rlp.IsEmpty || index is < 0 or > 15)
        {
            return true;
        }

        Rlp.ValueDecoderContext ctx = new(rlp);

        try
        {
            ctx.ReadSequenceLength();

            // Skip to the requested index
            for (int i = 0; i < index; i++)
            {
                ctx.SkipItem();
            }

            int prefix = ctx.PeekByte();

            // Empty: 128 (0x80) = empty byte array, or single byte 0
            return prefix is 128 or 0;
        }
        catch (RlpException)
        {
            return true;
        }
    }

    /// <summary>
    /// Checks if a branch child at the given index is an inline node (not a hash reference).
    /// </summary>
    /// <param name="rlp">Raw RLP-encoded branch node.</param>
    /// <param name="index">Child index (0-15).</param>
    /// <returns>True if the child is an inline node, false if empty or hash reference.</returns>
    public static bool IsBranchChildInline(ReadOnlySpan<byte> rlp, int index)
    {
        if (rlp.IsEmpty || index is < 0 or > 15)
        {
            return false;
        }

        Rlp.ValueDecoderContext ctx = new(rlp);

        try
        {
            ctx.ReadSequenceLength();

            // Skip to the requested index
            for (int i = 0; i < index; i++)
            {
                ctx.SkipItem();
            }

            int prefix = ctx.PeekByte();

            // Inline node: prefix >= 192 (0xC0) = list
            // Not inline: 128 (empty), 160 (32-byte hash), or 0 (empty)
            return prefix >= 192;
        }
        catch (RlpException)
        {
            return false;
        }
    }
}
