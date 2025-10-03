// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Shared helper methods for RLP processing to avoid code duplication between 
/// ValueRlpStream, RlpStream, and ValueDecoderContext.
/// </summary>
internal static class RlpHelpers
{
    public const int SmallPrefixBarrier = 56;
    /// <summary>
    /// Branchless implementation of RLP prefix length calculation.
    /// See RLP specification: https://github.com/ethereum/wiki/wiki/RLP
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculatePrefixLength(byte prefixByte)
    {
        // RLP encoding rules:
        // - For a single byte < 0x80: prefix length is 0 (no prefix).
        // - For short strings (0x80..0xb7): prefix length is 1.
        // - For long strings (0xb8..0xbf): prefix length is 1 + (prefix - 0xb7).
        // - For short lists (0xc0..0xf7): prefix length is 1.
        // - For long lists (0xf8..0xff): prefix length is 1 + (prefix - 0xf7).
        //
        // The following bit manipulations encode these rules without branches:

        uint p = prefixByte; // The prefix byte (0..255)
        uint v = p >> 3;     // Used to classify the prefix range (0..31)
        uint r = v >> 4;     // r = 0 for <0x80, r = 1 for >=0x80 (single byte vs. prefixed)
        uint t = 1u + (p & 7u); // t = 1..8, used for long string/list prefix length

        // longMask is 1 for 0xB8..0xBF or 0xF8..0xFF (long string/list prefixes), else 0
        // v | 8u == 31u is true for v == 23 (0xB8..0xBF) or v == 31 (0xF8..0xFF)
        uint longMask = ((v | 8u) == 31u) ? 1u : 0u;

        // If longMask == 1, add t to r; else add 0. This selects the correct prefix length for long string/list.
        uint add = (0u - longMask) & t;
        return (int)(r + add);
    }

    /// <summary>
    /// Deserializes a length value from a byte reference using unsafe operations.
    /// This is shared between ValueRlpStream, RlpStream, and ValueDecoderContext.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DeserializeLengthRef(ref byte firstElement, int lengthOfLength)
    {
        int result = firstElement;
        if (result == 0)
        {
            ThrowInvalidData();
        }

        if (lengthOfLength == 1)
        {
            // Already read above
            // result = span[0];
        }
        else if (lengthOfLength == 2)
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref firstElement));
            }
            else
            {
                result = Unsafe.ReadUnaligned<ushort>(ref firstElement);
            }
        }
        else if (lengthOfLength == 3)
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1)))
                    | (result << 16);
            }
            else
            {
                result = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1))
                    | (result << 16);
            }
        }
        else
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref firstElement));
            }
            else
            {
                result = Unsafe.ReadUnaligned<int>(ref firstElement);
            }
        }

        return result;

        [DoesNotReturn]
        static void ThrowInvalidData()
        {
            throw new RlpException("Length starts with 0");
        }
    }

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedByteValue(int buffer0)
        => throw new RlpException($"Unexpected byte value {buffer0}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowInvalidLength(int actualLength, int decodedLength)
        => throw new RlpException($"Invalid actual length {actualLength} decoded {decodedLength}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowInvalidLength(int lengthOfLength)
        => throw new RlpException($"Invalid length of length = {lengthOfLength}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedPrefix(int prefix)
        => throw new RlpException($"Expected a sequence prefix to be in the range of <192, 255> and got {prefix}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowSequenceLengthTooLong()
        => throw new RlpException("Expected length of length less than or equal to 4");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedLength(int length)
        => throw new RlpException($"Expected length greater than or equal to 56 and was {length}");

    [DoesNotReturn, StackTraceHidden]
    public static uint ThrowNonCanonicalInteger(int position)
        => throw new RlpException($"Non-canonical integer (leading zero bytes) at position {position}");

    [DoesNotReturn, StackTraceHidden]
    public static uint ThrowUnexpectedIntegerLength(int length)
        => throw new RlpException($"Unexpected length of long value: {length}");
}
