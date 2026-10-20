// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Encodes and parses standard trie node RLP straight to and from spans, without a <see cref="TrieNode"/>.</summary>
/// <remarks>The output is byte-identical to <see cref="TrieNode"/> encoding: a branch value is always empty, and a
/// child is embedded only when its RLP is shorter than a hash.</remarks>
internal static class NodeRlp
{
    private const byte LeafFlag = 0x20;
    private const byte OddFlag = 0x10;
    private const byte ShortStringOffset = 0x80;
    private const byte LongStringOffset = 0xb7;
    private const byte ShortListOffset = 0xc0;
    private const byte LongListOffset = 0xf7;
    private const int MaxLengthOfLength = 4;

    /// <summary>One RLP item: <see cref="Encoded"/> includes its header, <see cref="Payload"/> does not.</summary>
    public readonly ref struct Item(bool isList, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> encoded)
    {
        public readonly bool IsList = isList;
        public readonly ReadOnlySpan<byte> Payload = payload;
        public readonly ReadOnlySpan<byte> Encoded = encoded;
    }

    /// <summary>Reads the first RLP item of <paramref name="data"/>.</summary>
    /// <returns>False when the header or payload runs past the data.</returns>
    public static bool NextItem(ReadOnlySpan<byte> data, out Item item, out ReadOnlySpan<byte> rest)
    {
        item = default;
        rest = default;
        if (data.IsEmpty) return false;

        byte first = data[0];
        bool isList = first >= ShortListOffset;
        int headerLength = 1;
        int payloadLength;
        if (first < ShortStringOffset)
        {
            headerLength = 0;
            payloadLength = 1;
        }
        else if (first <= LongStringOffset || (isList && first <= LongListOffset))
        {
            payloadLength = first - (isList ? ShortListOffset : ShortStringOffset);
        }
        else
        {
            int lengthOfLength = first - (isList ? LongListOffset : LongStringOffset);
            if (lengthOfLength > MaxLengthOfLength || data.Length <= lengthOfLength) return false;

            uint length = 0;
            foreach (byte lengthByte in data.Slice(1, lengthOfLength)) length = length << 8 | lengthByte;
            if (length > int.MaxValue) return false;
            headerLength += lengthOfLength;
            payloadLength = (int)length;
        }

        if (payloadLength > data.Length - headerLength) return false;

        int end = headerLength + payloadLength;
        item = new Item(isList, data[headerLength..end], data[..end]);
        rest = data[end..];
        return true;
    }

    /// <summary>Length of the RLP string that carries the hex prefix of a key of <paramref name="nibbleCount"/> nibbles.</summary>
    /// <remarks>A one-byte hex prefix is below 0x80, so it is its own RLP string.</remarks>
    public static int HexPrefixLength(int nibbleCount) => nibbleCount < 2 ? 1 : nibbleCount / 2 + 2;

    /// <summary>Writes the hex prefix of <paramref name="nibbles"/> as an RLP string.</summary>
    /// <returns>The number of bytes written, <see cref="HexPrefixLength"/>.</returns>
    public static int WriteHexPrefix(ReadOnlySpan<byte> nibbles, bool isLeaf, Span<byte> destination)
    {
        int position = 0;
        if (nibbles.Length >= 2) destination[position++] = (byte)(ShortStringOffset + nibbles.Length / 2 + 1);

        byte flag = isLeaf ? LeafFlag : (byte)0;
        if ((nibbles.Length & 1) == 1)
        {
            destination[position++] = (byte)(flag | OddFlag | nibbles[0]);
            nibbles = nibbles[1..];
        }
        else
        {
            destination[position++] = flag;
        }

        Span<byte> packed = destination.Slice(position, nibbles.Length / 2);
        Nibbles.PackNibbles(ref MemoryMarshal.GetReference(nibbles), ref MemoryMarshal.GetReference(packed), packed.Length);
        return position + packed.Length;
    }

    /// <summary>Number of key nibbles a hex prefix, given without its RLP header, encodes.</summary>
    public static int HexPrefixNibbleCount(ReadOnlySpan<byte> hexPrefix) => (hexPrefix.Length - 1) * 2 + ((hexPrefix[0] & OddFlag) != 0 ? 1 : 0);

    public static bool IsLeafHexPrefix(ReadOnlySpan<byte> hexPrefix) => (hexPrefix[0] & LeafFlag) != 0;

    /// <summary>Decodes the key nibbles of a hex prefix, given without its RLP header, dropping the first <paramref name="skip"/>.</summary>
    public static void ReadHexPrefix(ReadOnlySpan<byte> hexPrefix, int skip, Span<byte> nibbles)
    {
        // Key nibble i sits at nibble i + 1 of the prefix when the key is odd, and after the padding nibble when even.
        int prefixNibble = skip + ((hexPrefix[0] & OddFlag) != 0 ? 1 : 2);
        if ((prefixNibble & 1) == 1)
        {
            nibbles[0] = (byte)(hexPrefix[prefixNibble >> 1] & 0x0F);
            nibbles = nibbles[1..];
            prefixNibble++;
        }

        ReadOnlySpan<byte> packed = hexPrefix[(prefixNibble >> 1)..];
        Nibbles.ExpandNibbles(ref MemoryMarshal.GetReference(packed), ref MemoryMarshal.GetReference(nibbles[..(packed.Length * 2)]), packed.Length);
    }

    public static int LeafLength(ReadOnlySpan<byte> keyNibbles, ReadOnlySpan<byte> value)
        => Rlp.LengthOfSequence(LeafPayloadLength(keyNibbles, value));

    /// <returns>The number of bytes written, <see cref="LeafLength"/>.</returns>
    public static int WriteLeaf(ReadOnlySpan<byte> keyNibbles, ReadOnlySpan<byte> value, Span<byte> destination)
    {
        int position = Rlp.StartSequence(destination, 0, LeafPayloadLength(keyNibbles, value));
        position += WriteHexPrefix(keyNibbles, isLeaf: true, destination[position..]);
        return position + Rlp.Encode(value, destination[position..]);
    }

    public static int ExtensionLength(ReadOnlySpan<byte> keyNibbles, in ChildRef child)
        => Rlp.LengthOfSequence(HexPrefixLength(keyNibbles.Length) + child.EncodedLength);

    /// <returns>The number of bytes written, <see cref="ExtensionLength"/>.</returns>
    public static int WriteExtension(ReadOnlySpan<byte> keyNibbles, in ChildRef child, Span<byte> destination)
    {
        int position = Rlp.StartSequence(destination, 0, HexPrefixLength(keyNibbles.Length) + child.EncodedLength);
        position += WriteHexPrefix(keyNibbles, isLeaf: false, destination[position..]);
        return position + child.Write(destination[position..]);
    }

    public static int BranchLength(ReadOnlySpan<ChildRef> children) => Rlp.LengthOfSequence(BranchPayloadLength(children));

    /// <param name="children">The sixteen children, in index order.</param>
    /// <returns>The number of bytes written, <see cref="BranchLength"/>.</returns>
    public static int WriteBranch(ReadOnlySpan<ChildRef> children, Span<byte> destination)
    {
        int position = Rlp.StartSequence(destination, 0, BranchPayloadLength(children));
        foreach (ref readonly ChildRef child in children)
        {
            position += child.Write(destination[position..]);
        }

        destination[position++] = Rlp.EmptyByteArrayByte;
        return position;
    }

    private static int LeafPayloadLength(ReadOnlySpan<byte> keyNibbles, ReadOnlySpan<byte> value)
        => HexPrefixLength(keyNibbles.Length) + Rlp.LengthOf(value);

    private static int BranchPayloadLength(ReadOnlySpan<ChildRef> children)
    {
        // The value slot is always empty.
        int length = 1;
        foreach (ref readonly ChildRef child in children)
        {
            length += child.EncodedLength;
        }

        return length;
    }
}
