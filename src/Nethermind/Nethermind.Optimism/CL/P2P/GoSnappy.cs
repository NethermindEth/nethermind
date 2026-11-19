// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Optimism.CL.P2P;

/// <summary>
/// Decodes Snappy blocks, accepting exactly the inputs that golang/snappy v1.0.0 accepts.
/// </summary>
/// <remarks>
/// <para>
/// The block format is google/snappy's <c>format_description.txt</c>. Which inputs are valid follows golang/snappy
/// v1.0.0, the decoder op-node uses for gossip message IDs and block validation: the length header is a uvarint of
/// 1 to 10 bytes whose value fits in 32 bits, and the elements after it must consume the whole input and produce
/// exactly the declared length.
/// </para>
/// <para>
/// Do not replace this class with a general Snappy library. The gossip message ID hashes the decoded bytes only when
/// the input is valid, so an input on which a library and op-node disagree gets a different message ID than on
/// op-node. Snappier, for example, accepts bytes after a complete block and rejects headers longer than 5 bytes.
/// </para>
/// </remarks>
internal static class GoSnappy
{
    private const int MaxHeaderLength = 10;
    private const int TagLiteral = 0b00;
    private const int TagCopy1 = 0b01;
    private const int TagCopy2 = 0b10;

    /// <summary>Reads the decoded length from the header of a Snappy block.</summary>
    /// <param name="block">The block, starting with its length header.</param>
    /// <param name="length">The decoded length.</param>
    /// <param name="headerLength">The number of bytes the header occupies.</param>
    /// <returns><c>false</c> if the header is not a uvarint of 1 to 10 bytes or its value does not fit in 32 bits.</returns>
    public static bool TryReadDecodedLength(ReadOnlySpan<byte> block, out uint length, out int headerLength)
    {
        ulong value = 0;
        for (int i = 0; i < block.Length && i < MaxHeaderLength; i++)
        {
            byte b = block[i];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if (b < 0x80)
            {
                // As in encoding/binary.Uvarint, the tenth byte holds bit 63 only; higher bits would be shifted out.
                if ((i == MaxHeaderLength - 1 && b > 1) || value > uint.MaxValue) break;

                length = (uint)value;
                headerLength = i + 1;
                return true;
            }
        }

        length = 0;
        headerLength = 0;
        return false;
    }

    /// <summary>Decodes a Snappy block into a destination of exactly the declared length.</summary>
    /// <param name="block">The whole block, length header included.</param>
    /// <param name="destination">The output; its length must equal the declared length.</param>
    /// <returns>
    /// <c>true</c> if the block is valid and declares the length of <paramref name="destination"/>; otherwise
    /// <c>false</c>, and the content of <paramref name="destination"/> is unspecified.
    /// </returns>
    public static bool TryDecode(ReadOnlySpan<byte> block, Span<byte> destination)
    {
        if (!TryReadDecodedLength(block, out uint length, out int headerLength) || length != (uint)destination.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> input = block[headerLength..];
        int s = 0;
        int d = 0;
        // Run until the input ends, not until the output is full, so bytes after the last expected element make the
        // block invalid: a complete element would overflow the output, since every element produces at least one
        // byte, and a cut-off element fails the input bound.
        while (s < input.Length)
        {
            byte tag = input[s++];
            if ((tag & 0b11) == TagLiteral)
            {
                // (length - 1) is in the upper six bits, or, when they read 60 to 63, in the next 1 to 4 bytes.
                uint lengthMinusOne = (uint)(tag >> 2);
                if (lengthMinusOne >= 60)
                {
                    int lengthBytes = (int)lengthMinusOne - 59;
                    if (lengthBytes > input.Length - s) return false;

                    lengthMinusOne = 0;
                    for (int i = 0; i < lengthBytes; i++)
                    {
                        lengthMinusOne |= (uint)input[s + i] << (8 * i);
                    }

                    s += lengthBytes;
                }

                long literalLength = lengthMinusOne + 1L;
                if (literalLength > input.Length - s || literalLength > destination.Length - d) return false;

                input.Slice(s, (int)literalLength).CopyTo(destination[d..]);
                s += (int)literalLength;
                d += (int)literalLength;
                continue;
            }

            int copyLength;
            uint offset;
            switch (tag & 0b11)
            {
                case TagCopy1:
                    if (input.Length - s < 1) return false;
                    copyLength = 4 + ((tag >> 2) & 0b111);
                    offset = ((uint)(tag & 0xE0) << 3) | input[s];
                    s += 1;
                    break;
                case TagCopy2:
                    if (input.Length - s < 2) return false;
                    copyLength = 1 + (tag >> 2);
                    offset = BinaryPrimitives.ReadUInt16LittleEndian(input[s..]);
                    s += 2;
                    break;
                default: // copy with a 4-byte offset
                    if (input.Length - s < 4) return false;
                    copyLength = 1 + (tag >> 2);
                    offset = BinaryPrimitives.ReadUInt32LittleEndian(input[s..]);
                    s += 4;
                    break;
            }

            if (offset == 0 || offset > (uint)d || copyLength > destination.Length - d) return false;

            int from = d - (int)offset;
            int end = d + copyLength;
            // When offset < copyLength, the copy repeats the last offset bytes; one CopyTo has memmove semantics and
            // would copy the original bytes instead. Each chunk is at most d - from long, so it never overlaps its
            // source, and d - from stays a multiple of offset, so the repeated region doubles with each chunk.
            while (d < end)
            {
                int chunk = Math.Min(d - from, end - d);
                destination.Slice(from, chunk).CopyTo(destination[d..]);
                d += chunk;
            }
        }

        return d == destination.Length;
    }
}
