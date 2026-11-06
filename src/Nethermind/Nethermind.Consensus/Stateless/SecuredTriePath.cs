// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>Checks the paths of witness nodes of the state and storage tries, whose keys are keccaks of 64 nibbles.</summary>
/// <remarks>
/// Each node a trie operation visits is checked against its depth. A node off every visited path is checked against its
/// depth by <see cref="WitnessTriePaths"/>' walk. A witness node no decoded trie reaches is left unchecked, as the spec
/// only hashes it.
/// </remarks>
internal static class SecuredTriePath
{
    public const int KeyNibbles = 2 * Hash256.Size;

    /// <summary>Rejects a node that cannot sit <paramref name="depth"/> nibbles below a root: a leaf whose path does not end at
    /// the key, an extension whose path is empty or reaches it, or any other node at the key's length.</summary>
    /// <exception cref="TrieException">The node's path does not end at the key.</exception>
    public static void ValidateAtDepth(ReadOnlySpan<byte> rlp, int depth)
    {
        int remaining = KeyNibbles - depth;
        if (TryGetPath(rlp, out int nibbles, out bool isLeaf) ? isLeaf ? nibbles != remaining : nibbles == 0 || nibbles >= remaining : remaining <= 0)
            ThrowPathExceedsKey();
    }

    [DoesNotReturn]
    public static void ThrowPathExceedsKey() =>
        throw new TrieException("A witness trie path does not end at the length of its hashed keys");

    /// <summary>Gets the length of the hex-prefix path of a leaf or an extension.</summary>
    /// <returns>Whether <paramref name="rlp"/> is a list of two items whose first is a non-empty string.</returns>
    internal static bool TryGetPath(ReadOnlySpan<byte> rlp, out int nibbles, out bool isLeaf)
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
    internal static bool TryReadItem(ReadOnlySpan<byte> rlp, int position, out bool isList, out int start, out int end)
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
