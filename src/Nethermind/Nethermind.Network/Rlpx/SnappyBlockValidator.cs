// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Network.Rlpx;

internal static class SnappyBlockValidator
{
    /// <summary>Checks that a raw Snappy block consumes all input and produces exactly its declared length.</summary>
    /// <remarks>
    /// Snappier accepts trailing commands after reaching the declared length, so peer input needs a strict structural
    /// pass before decompression.
    /// </remarks>
    public static bool IsValid(ReadOnlySpan<byte> input, int expectedLength)
    {
        if ((uint)expectedLength > SnappyParameters.MaxSnappyLength ||
            !TryReadUncompressedLength(input, out int position, out uint declaredLength) ||
            declaredLength != (uint)expectedLength)
        {
            return false;
        }

        int written = 0;
        while (position < input.Length)
        {
            byte tag = input[position++];
            switch (tag & 0x03)
            {
                case 0:
                    if (!TryReadLiteral(input, tag, expectedLength - written, ref position, out int literalLength))
                    {
                        return false;
                    }

                    written += literalLength;
                    break;
                case 1:
                    if (position >= input.Length ||
                        !TryApplyCopy(
                            4 + ((tag >> 2) & 0x07),
                            (uint)((tag & 0xe0) << 3) | input[position++],
                            expectedLength,
                            ref written))
                    {
                        return false;
                    }

                    break;
                case 2:
                    if (input.Length - position < sizeof(ushort) ||
                        !TryApplyCopy(
                            1 + (tag >> 2),
                            BinaryPrimitives.ReadUInt16LittleEndian(input[position..]),
                            expectedLength,
                            ref written))
                    {
                        return false;
                    }

                    position += sizeof(ushort);
                    break;
                default:
                    if (input.Length - position < sizeof(uint) ||
                        !TryApplyCopy(
                            1 + (tag >> 2),
                            BinaryPrimitives.ReadUInt32LittleEndian(input[position..]),
                            expectedLength,
                            ref written))
                    {
                        return false;
                    }

                    position += sizeof(uint);
                    break;
            }
        }

        return written == expectedLength;
    }

    private static bool TryReadUncompressedLength(ReadOnlySpan<byte> input, out int position, out uint length)
    {
        length = 0;
        position = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            if (position >= input.Length)
            {
                return false;
            }

            byte current = input[position++];
            if (shift == 28 && (current & 0xf0) != 0)
            {
                return false;
            }

            length |= (uint)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLiteral(
        ReadOnlySpan<byte> input,
        byte tag,
        int remainingOutput,
        ref int position,
        out int literalLength)
    {
        int lengthCode = tag >> 2;
        if (lengthCode < 60)
        {
            literalLength = lengthCode + 1;
        }
        else
        {
            int lengthBytes = lengthCode - 59;
            if (input.Length - position < lengthBytes)
            {
                literalLength = 0;
                return false;
            }

            uint lengthMinusOne = lengthBytes switch
            {
                1 => input[position],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(input[position..]),
                3 => (uint)(input[position] | input[position + 1] << 8 | input[position + 2] << 16),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(input[position..])
            };
            position += lengthBytes;

            if (lengthMinusOne >= (uint)remainingOutput)
            {
                literalLength = 0;
                return false;
            }

            literalLength = (int)lengthMinusOne + 1;
        }

        if (literalLength > remainingOutput || input.Length - position < literalLength)
        {
            return false;
        }

        position += literalLength;
        return true;
    }

    private static bool TryApplyCopy(int length, uint offset, int expectedLength, ref int written)
    {
        if (offset == 0 || offset > (uint)written || length > expectedLength - written)
        {
            return false;
        }

        written += length;
        return true;
    }
}
