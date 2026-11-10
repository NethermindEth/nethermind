// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    // The list header of a full branch, 0xf9 then its payload length in two bytes, and its first child's prefix, read little endian.
    private const uint FullBranchStart = 0xf9 | (FullBranchLength - 3) >> 8 << 8 | ((FullBranchLength - 3) & 0xff) << 16 | (uint)HashString << 24;

    /// <summary>Rejects a witness node that is malformed or cannot sit <paramref name="depth"/> nibbles below its trie's root.</summary>
    /// <param name="rlp">The node's encoding.</param>
    /// <param name="depth">The number of nibbles from the root to the node.</param>
    /// <param name="isHashed">Whether the node is referenced by its hash rather than embedded in its parent.</param>
    /// <exception cref="TrieException">The node is malformed, or its path does not end at the key length.</exception>
    /// <remarks>
    /// The encoding must be a leaf whose path ends at the key, an extension whose path stops short of it, or a branch above it
    /// with no value and at least two children. Each child reference must be empty, a hash, or an embedded node shorter than a
    /// hash, and a hashed node below the root must be too long to embed.
    /// <para>
    /// Every witness node a block reaches is checked, so this is on the guest's hot path: it reads the encoding unchecked, each
    /// read below a position it has bounded, and tells a full branch of hashed children, most of the nodes, from its 17 item
    /// prefixes alone.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Validate(ReadOnlySpan<byte> rlp, int depth, bool isHashed)
    {
        int remaining = KeyNibbles - depth;
        int length = rlp.Length;
        ref byte node = ref MemoryMarshal.GetReference(rlp);
        if (length == FullBranchLength)
        {
            // Unrolled: a loop costs the guest more than twice the three instructions per prefix. The list header and the first
            // child's prefix are read as one word: an array's data is word aligned.
            uint mismatch = (Unsafe.ReadUnaligned<uint>(ref node) ^ FullBranchStart) | (uint)(Unsafe.Add(ref node, FullBranchLength - 1) ^ EmptyString)
                | NotHash(ref node, 1) | NotHash(ref node, 2) | NotHash(ref node, 3)
                | NotHash(ref node, 4) | NotHash(ref node, 5) | NotHash(ref node, 6) | NotHash(ref node, 7)
                | NotHash(ref node, 8) | NotHash(ref node, 9) | NotHash(ref node, 10) | NotHash(ref node, 11)
                | NotHash(ref node, 12) | NotHash(ref node, 13) | NotHash(ref node, 14) | NotHash(ref node, 15);

            if (mismatch == 0)
            {
                if (remaining <= 0) ThrowInvalidNode();
                return;
            }
        }

        // Only the root is referenced by hash whatever its length: a shorter descendant is embedded in its parent. A node
        // encodes in under 64 KiB, so its list header is at most three bytes.
        if (length < (isHashed && depth > 0 ? Hash256.Size : 1)) ThrowInvalidNode();
        int header = node;
        int itemsStart = header <= 0xf7 ? 1 : header - 0xf6;
        int listEnd = header switch
        {
            < 0xc0 => -1,
            <= 0xf7 => 1 + header - 0xc0,
            0xf8 when length > 1 => 2 + Unsafe.Add(ref node, 1),
            0xf9 when length > 2 => 3 + (Unsafe.Add(ref node, 1) << 8 | Unsafe.Add(ref node, 2)),
            _ => -1,
        };
        if (listEnd != length || itemsStart >= length) ThrowInvalidNode();

        // Most of the other nodes are branches, so a branch is tried first; a leaf or an extension fails it within its first
        // three items. Unrolled: the loop's counter costs the guest a quarter of each slot.
        int position = itemsStart;
        int children = 0;
        if (SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            && SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            && SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            && SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            && SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            && SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            && SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            && SkipChild(ref node, ref position, length, ref children) && SkipChild(ref node, ref position, length, ref children)
            // A secured key never ends at a branch, so a branch holds no value.
            && position + 1 == length && Unsafe.Add(ref node, position) == EmptyString)
        {
            if (remaining <= 0 || children < 2) ThrowInvalidNode();
            return;
        }

        int secondItem = ItemEnd(ref node, itemsStart, length);
        if (secondItem < 0 || secondItem >= length || ItemEnd(ref node, secondItem, length) != length) ThrowInvalidNode();
        ValidateLeafOrExtension(ref node, itemsStart, secondItem, length, remaining);
    }

    /// <summary>Moves <paramref name="position"/>, below <paramref name="length"/>, past a branch's child reference: empty, a
    /// hash, or an embedded node shorter than a hash.</summary>
    /// <returns>Whether the item there is one; <paramref name="position"/> is then past it, though maybe past the node.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SkipChild(ref byte node, ref int position, int length, ref int children)
    {
        if (position >= length) return false;

        int prefix = Unsafe.Add(ref node, position);
        if (prefix == EmptyString)
        {
            position++;
            return true;
        }

        children++;
        if (prefix == HashString)
        {
            position += 1 + Hash256.Size;
            return true;
        }

        // An embedded node, whose list encoding is shorter than a hash.
        position += 1 + prefix - 0xc0;
        return (uint)(prefix - 0xc1) < Hash256.Size - 2;
    }

    /// <summary>Nonzero unless child <paramref name="index"/> of a full branch is a hash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NotHash(ref byte fullBranch, int index) => (uint)(Unsafe.Add(ref fullBranch, 3 + index * (1 + Hash256.Size)) ^ HashString);

    /// <summary>Checks a node of two items, the first of which ends at <paramref name="secondItem"/>.</summary>
    private static void ValidateLeafOrExtension(ref byte node, int path, int secondItem, int length, int remaining)
    {
        // A path of 56 bytes or more, long enough for a long string, is far longer than a key.
        int prefix = Unsafe.Add(ref node, path);
        if (prefix == EmptyString || prefix > 0xb7) ThrowInvalidNode();

        int pathBytes = secondItem - path - (prefix < 0x80 ? 0 : 1);
        // Hex-prefix encoding: the first nibble flags a leaf (0x2) and an odd nibble count (0x1), whose first nibble then
        // shares the byte.
        int flags = Unsafe.Add(ref node, secondItem - pathBytes) >> 4;
        int nibbles = 2 * pathBytes - ((flags & 1) != 0 ? 1 : 2);
        int child = Unsafe.Add(ref node, secondItem);
        bool isValid = (flags & 2) != 0
            ? nibbles == remaining && child < 0xc0
            // A child reference that is a hash, or an embedded node shorter than one.
            : nibbles > 0 && nibbles < remaining && (child == HashString || (uint)(child - 0xc1) < Hash256.Size - 2);
        if (!isValid) ThrowInvalidNode();
    }

    /// <summary>Gets where the item at <paramref name="position"/>, below <paramref name="end"/>, ends, or -1 if it runs past it.</summary>
    private static int ItemEnd(ref byte node, int position, int end)
    {
        int prefix = Unsafe.Add(ref node, position);
        int itemEnd = prefix switch
        {
            < 0x80 => position + 1,
            <= 0xb7 => position + 1 + prefix - 0x80,
            < 0xc0 => LongItemEnd(ref node, position, prefix - 0xb7, end),
            <= 0xf7 => position + 1 + prefix - 0xc0,
            _ => LongItemEnd(ref node, position, prefix - 0xf7, end),
        };
        return itemEnd <= end ? itemEnd : -1;
    }

    private static int LongItemEnd(ref byte node, int position, int lengthOfLength, int end)
    {
        if (lengthOfLength > 2 || position + lengthOfLength >= end) return -1;

        int length = 0;
        for (int i = 1; i <= lengthOfLength; i++) length = length << 8 | Unsafe.Add(ref node, position + i);
        return position + 1 + lengthOfLength + length;
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
