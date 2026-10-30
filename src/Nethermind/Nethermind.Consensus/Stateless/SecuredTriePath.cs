// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>Checks the paths of witness nodes of the state and storage tries, whose keys are keccaks of 64 nibbles.</summary>
/// <remarks>
/// Two checks stand in for a walk of every witness node reachable from a root: each witness node's own path is checked once
/// (<see cref="ValidateWitnessNode"/>), and each node a trie operation visits is checked against its depth, so a node off
/// every visited path is never checked against one.
/// </remarks>
internal static class SecuredTriePath
{
    public const int KeyNibbles = 2 * Hash256.Size;

    /// <summary>Rejects a leaf whose path is longer than a key, or an extension whose path is empty or not shorter than a key.</summary>
    /// <param name="rlp">A witness node; one that is not a two-item list starting with a non-empty string is left alone.</param>
    /// <exception cref="TrieException">The node's path cannot fit a key.</exception>
    /// <remarks>
    /// Only a path of one byte, an empty extension's, or of 33 bytes or more can fail, so a node whose first item starts
    /// otherwise is passed on that byte alone: every branch, whose first item is empty, a keccak or an inlined node, and
    /// nearly every leaf.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ValidateWitnessNode(ReadOnlySpan<byte> rlp)
    {
        if (rlp.IsEmpty) return;

        int first = rlp[0] < 0xf8 ? 1 : rlp[0] - 0xf6;
        if ((uint)first >= (uint)rlp.Length) return;

        int prefix = rlp[first];
        if (prefix < 0x10 || prefix == 0x81 || (uint)(prefix - 0xa1) <= 0xbf - 0xa1) ValidatePath(rlp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ValidatePath(ReadOnlySpan<byte> rlp)
    {
        if (TryGetPath(rlp, out int nibbles, out bool isLeaf) && (isLeaf ? nibbles > KeyNibbles : nibbles is 0 or >= KeyNibbles))
            ThrowPathExceedsKey();
    }

    /// <summary>Rejects a node that cannot sit <paramref name="depth"/> nibbles below a root: a leaf whose path does not end at
    /// the key, an extension whose path reaches it, or any other node at the key's length.</summary>
    /// <exception cref="TrieException">The node's path does not end at the key.</exception>
    public static void ValidateAtDepth(ReadOnlySpan<byte> rlp, int depth)
    {
        int remaining = KeyNibbles - depth;
        if (TryGetPath(rlp, out int nibbles, out bool isLeaf) ? isLeaf ? nibbles != remaining : nibbles >= remaining : remaining <= 0)
            ThrowPathExceedsKey();
    }

    [DoesNotReturn]
    public static void ThrowPathExceedsKey() =>
        throw new TrieException("A witness trie path does not end at the length of its hashed keys");

    /// <summary>Gets the length of the hex-prefix path of a leaf or an extension.</summary>
    /// <returns>Whether <paramref name="rlp"/> is a list of two items whose first is a non-empty string.</returns>
    private static bool TryGetPath(ReadOnlySpan<byte> rlp, out int nibbles, out bool isLeaf)
    {
        nibbles = 0;
        isLeaf = false;
        if (!TryReadItem(rlp, 0, out bool isList, out int start, out int end) || !isList || end != rlp.Length
            || !TryReadItem(rlp, start, out isList, out int pathStart, out int pathEnd) || isList || pathStart == pathEnd
            || !TryReadItem(rlp, pathEnd, out _, out _, out int valueEnd) || valueEnd != end)
        {
            return false;
        }

        // Hex-prefix encoding: the first nibble flags a leaf (0x2, decoded as any flag from it up) and an odd nibble
        // count (0x1), whose first nibble then shares the byte.
        int flags = rlp[pathStart];
        isLeaf = flags >= 0x20;
        nibbles = 2 * (pathEnd - pathStart) - ((flags & 0x10) != 0 ? 1 : 2);
        return true;
    }

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
