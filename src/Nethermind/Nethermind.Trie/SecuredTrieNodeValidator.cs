// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

/// <summary>Checks the witness nodes of the state and storage tries, whose keys are keccaks of 64 nibbles, as trie operations reach them.</summary>
/// <remarks>
/// Mirrors the checks of <c>_resolve</c> in the zkEVM spec's <c>incremental_mpt.py</c>: <c>_decode_witness_node</c> on the
/// node's own encoding and <c>_check_secured_path</c> on its depth. Each node is checked when an operation first reaches it, so
/// a malformed node off the block's path does not fail the block.
/// </remarks>
internal static class SecuredTrieNodeValidator
{
    public const int KeyNibbles = 2 * Hash256.Size;

    private const int EmptyString = 0x80;
    private const int HashString = 0x80 + Hash256.Size;
    private const int BranchChildren = 16;
    private const int FullBranchLength = 3 + BranchChildren * (1 + Hash256.Size) + 1;

    /// <summary>Rejects a witness node that is malformed or cannot sit <paramref name="depth"/> nibbles below its trie's root.</summary>
    /// <param name="rlp">The node's encoding.</param>
    /// <param name="depth">The number of nibbles from the root to the node.</param>
    /// <param name="isHashed">Whether the node is referenced by its hash rather than embedded in its parent.</param>
    /// <exception cref="TrieException">The node is malformed, or its path does not end at the key length.</exception>
    /// <remarks>
    /// The encoding must be a leaf whose path ends at the key, an extension whose path stops short of it, or a branch above it
    /// with no value and at least two children. Each child reference must be empty, a hash, or an embedded node shorter than a
    /// hash, and a hashed node below the root must be too long to embed. A full branch of hashed children is told from its
    /// item prefixes alone.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Validate(ReadOnlySpan<byte> rlp, int depth, bool isHashed)
    {
        int remaining = KeyNibbles - depth;
        if (rlp.Length == FullBranchLength && IsFullBranch(rlp))
        {
            if (remaining <= 0) ThrowInvalidNode();
            return;
        }

        // Only the root is referenced by hash whatever its length: a shorter descendant is embedded in its parent.
        if (isHashed && depth > 0 && rlp.Length < Hash256.Size) ThrowInvalidNode();
        if (!TryReadItem(rlp, 0, out bool isList, out int position, out int end) || !isList || end != rlp.Length) ThrowInvalidNode();

        if (!TryReadItem(rlp, position, out isList, out int pathStart, out int pathEnd)) ThrowInvalidNode();
        if (!TryReadItem(rlp, pathEnd, out bool isSecondList, out int secondStart, out int secondEnd)) ThrowInvalidNode();

        if (secondEnd == end)
        {
            if (isList || pathStart == pathEnd) ThrowInvalidNode();
            // Hex-prefix encoding: the first nibble flags a leaf (0x2) and an odd nibble count (0x1), whose first nibble then
            // shares the byte.
            int flags = rlp[pathStart] >> 4;
            int nibbles = 2 * (pathEnd - pathStart) - ((flags & 1) != 0 ? 1 : 2);
            if ((flags & 2) != 0
                ? isSecondList || nibbles != remaining
                : nibbles == 0 || nibbles >= remaining || !IsChildReference(rlp, pathEnd, isSecondList, secondEnd) || secondEnd == secondStart)
            {
                ThrowInvalidNode();
            }

            return;
        }

        if (remaining <= 0) ThrowInvalidNode();
        int children = 0;
        int itemPosition = position;
        for (int i = 0; i < BranchChildren; i++)
        {
            if (!TryReadItem(rlp, itemPosition, out isList, out int start, out int itemEnd) || !IsChildReference(rlp, itemPosition, isList, itemEnd))
                ThrowInvalidNode();

            if (itemEnd != start) children++;
            itemPosition = itemEnd;
        }

        // A secured key never ends at a branch, so a branch holds no value.
        if (!TryReadItem(rlp, itemPosition, out isList, out int valueStart, out int valueEnd) || isList || valueEnd != valueStart || valueEnd != end || children < 2)
            ThrowInvalidNode();
    }

    /// <summary>Gets the child reference of an extension: its hash, or the encoding of a child embedded in it.</summary>
    /// <param name="rlp">A node that <see cref="Validate"/> passed.</param>
    /// <returns>Whether <paramref name="rlp"/> is an extension.</returns>
    public static bool TryGetExtensionChild(ReadOnlySpan<byte> rlp, out ReadOnlySpan<byte> child, out bool isHashed)
    {
        child = default;
        isHashed = false;
        if (rlp.Length == FullBranchLength
            || !TryReadItem(rlp, 0, out _, out int position, out int end)
            || !TryReadItem(rlp, position, out _, out int pathStart, out int pathEnd)
            || !TryReadItem(rlp, pathEnd, out bool isList, out int childStart, out int childEnd)
            || childEnd != end
            || (rlp[pathStart] & 0x20) != 0)
        {
            return false;
        }

        isHashed = !isList;
        child = isList ? rlp[pathEnd..childEnd] : rlp[childStart..childEnd];
        return true;
    }

    /// <summary>Rejects the child of an extension that is not a branch, which would make the two paths one.</summary>
    /// <exception cref="TrieException"><paramref name="rlp"/> is not a list of more than two items.</exception>
    public static void ValidateExtensionChild(ReadOnlySpan<byte> rlp)
    {
        if (!TryReadItem(rlp, 0, out bool isList, out int position, out int end) || !isList
            || !TryReadItem(rlp, position, out _, out _, out position)
            || !TryReadItem(rlp, position, out _, out _, out position)
            || position >= end)
        {
            ThrowInvalidNode();
        }
    }

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowInvalidNode() => throw new TrieException("A witness trie node is malformed or its path does not end at the length of its hashed keys");

    /// <summary>Whether <paramref name="rlp"/>, <see cref="FullBranchLength"/> long, is a branch of hashed children with no value.</summary>
    private static bool IsFullBranch(ReadOnlySpan<byte> rlp)
    {
        if (rlp[0] != 0xf9 || rlp[1] != (FullBranchLength - 3) >> 8 || rlp[2] != ((FullBranchLength - 3) & 0xff) || rlp[^1] != EmptyString)
            return false;

        for (int position = 3; position < FullBranchLength - 1; position += 1 + Hash256.Size)
        {
            if (rlp[position] != HashString) return false;
        }

        return true;
    }

    /// <summary>Whether the item at <paramref name="position"/> is an empty reference, a hash, or a node embedded because it is
    /// shorter than a hash.</summary>
    private static bool IsChildReference(ReadOnlySpan<byte> rlp, int position, bool isList, int end) =>
        isList ? end - position < Hash256.Size : rlp[position] is EmptyString or HashString;

    /// <summary>Reads the header of the RLP item at <paramref name="position"/>.</summary>
    /// <param name="start">Where the item's content starts: the item itself for a single byte below 0x80.</param>
    /// <param name="end">Where the item ends.</param>
    /// <returns>Whether the item lies within <paramref name="rlp"/>.</returns>
    private static bool TryReadItem(ReadOnlySpan<byte> rlp, int position, out bool isList, out int start, out int end)
    {
        isList = false;
        start = end = 0;
        if ((uint)position >= (uint)rlp.Length) return false;

        int prefix = rlp[position];
        if (prefix < 0x80)
        {
            start = position;
            end = position + 1;
            return true;
        }

        isList = prefix >= 0xc0;
        int shortLength = prefix - (isList ? 0xc0 : 0x80);
        long length = shortLength;
        start = position + 1;
        if (shortLength > 55)
        {
            int lengthOfLength = shortLength - 55;
            if (lengthOfLength > sizeof(int) || start + lengthOfLength > rlp.Length) return false;

            length = 0;
            for (int i = 0; i < lengthOfLength; i++) length = length << 8 | rlp[start + i];
            start += lengthOfLength;
        }

        if (start + length > rlp.Length) return false;

        end = start + (int)length;
        return true;
    }
}
