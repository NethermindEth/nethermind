// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Shared helper methods for RLP processing to avoid code duplication between
/// ValueRlpStream, RlpStream, and ValueDecoderContext.
/// </summary>
internal static class RlpHelpers
{
    public const int SmallPrefixBarrier = 56;

    // RLP prefix boundaries (Ethereum Yellow Paper, Appendix B)
    private const int ShortStringOffset = 0x80;     // 128 — first short-string prefix
    private const int ShortStringMaxPrefix = 0xB7;  // 183 — last short-string prefix
    private const int ListOffset = 0xC0;            // 192 — first list prefix
    private const int ShortListMaxPrefix = 0xF7;    // 247 — last short-list prefix

    // RVA static data — embedded in the assembly binary, no heap allocation, no GC root,
    // no CORINFO_HELP_GET_GCSTATIC_BASE in any JIT tier.

    /// <summary>
    /// Prefix length for each RLP prefix byte (0 for single byte, 1 for short, 2-9 for long).
    /// </summary>
    private static ReadOnlySpan<byte> PrefixLengthData =>
    [
        // 0-15: single byte (prefix length = 0)
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 16-31
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 32-47
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 48-63
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 64-79
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 80-95
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 96-111
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 112-127
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 128-143: short string (prefix length = 1)
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        // 144-159
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        // 160-175
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        // 176-191: 176-183 short string (1), 184-191 long string (2-9)
        1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        // 192-207: short list (prefix length = 1)
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        // 208-223
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        // 224-239
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        // 240-255: 240-247 short list (1), 248-255 long list (2-9)
        1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9,
    ];

    /// <summary>
    /// Total RLP item length (prefix + content) for short-form prefixes.
    /// 0 = sentinel for long-form prefixes (184-191, 248-255) that require extended length decoding.
    /// </summary>
    private static ReadOnlySpan<byte> TotalRlpLengthData =>
    [
        // 0-15: single byte value → total length = 1
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 16-31
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 32-47
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 48-63
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 64-79
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 80-95
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 96-111
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 112-127
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // 128-143: short string → total = i - 127 (1..16)
         1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,
        // 144-159: (17..32)
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        // 160-175: (33..48)
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        // 176-191: 176-183 short string (49..56), 184-191 long string (sentinel = 0)
        49, 50, 51, 52, 53, 54, 55, 56,  0,  0,  0,  0,  0,  0,  0,  0,
        // 192-207: short list → total = i - 191 (1..16)
         1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,
        // 208-223: (17..32)
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        // 224-239: (33..48)
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        // 240-255: 240-247 short list (49..56), 248-255 long list (sentinel = 0)
        49, 50, 51, 52, 53, 54, 55, 56,  0,  0,  0,  0,  0,  0,  0,  0,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPrefixLength(int prefixByte)
    {
        Debug.Assert((uint)prefixByte <= byte.MaxValue);
        return Unsafe.Add(ref MemoryMarshal.GetReference(PrefixLengthData), prefixByte);
    }

    /// <summary>
    /// Returns the total RLP item length (prefix + content) for short-form prefixes.
    /// Returns 0 for long-form prefixes (184-191, 248-255) that require additional length bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GetTotalRlpLength(int prefixByte)
    {
        Debug.Assert((uint)prefixByte <= byte.MaxValue);
        return Unsafe.Add(ref MemoryMarshal.GetReference(TotalRlpLengthData), prefixByte);
    }

    /// <summary>
    /// Returns the prefix length and content length of the RLP item at the given position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int PrefixLength, int ContentLength) PeekPrefixAndContentLength(
        ReadOnlySpan<byte> data, int position)
    {
        int prefix = data[position];
        return prefix switch
        {
            < ShortStringOffset => (0, 1),                                                                 // single byte value
            <= ShortStringMaxPrefix => (1, prefix - ShortStringOffset),                                    // short string
            < ListOffset => PeekLongPrefixAndContentLength(data, position, prefix - ShortStringMaxPrefix), // long string
            <= ShortListMaxPrefix => (1, prefix - ListOffset),                                             // short list
            _ => PeekLongPrefixAndContentLength(data, position, prefix - ShortListMaxPrefix)               // long list
        };
    }

    /// <summary>
    /// Returns the total RLP item length (prefix + content) of the item at the given position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PeekNextRlpLength(ReadOnlySpan<byte> data, int position)
    {
        int prefix = data[position];
        int totalLength = GetTotalRlpLength(prefix);
        return totalLength != 0
            ? totalLength
            : PeekLongRlpLength(data, position, prefix);
    }

    /// <summary>
    /// Counts the number of top-level RLP items in the given data range.
    /// </summary>
    public static int CountItems(ReadOnlySpan<byte> data, int position, int end, int maxSearch)
    {
        int numberOfItems = 0;
        while (position < end && numberOfItems < maxSearch)
        {
            position += PeekNextRlpLength(data, position);
            numberOfItems++;
        }
        return numberOfItems;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int PrefixLength, int ContentLength) PeekLongPrefixAndContentLength(
        ReadOnlySpan<byte> data, int position, int lengthOfLength)
    {
        if ((uint)lengthOfLength > 4)
        {
            ThrowSequenceLengthTooLong();
        }

        int contentLength = DeserializeLengthRef(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(data), position + 1),
            lengthOfLength);

        // Canonical RLP requires long-form encoding only when content length >= 56.
        // Accepting non-canonical lengths could cause consensus divergence between clients.
        if (contentLength < SmallPrefixBarrier)
        {
            ThrowUnexpectedLength(contentLength);
        }

        return (1 + lengthOfLength, contentLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PeekLongRlpLength(ReadOnlySpan<byte> data, int position, int prefix)
    {
        int lengthOfLength = prefix < ListOffset ? prefix - ShortStringMaxPrefix : prefix - ShortListMaxPrefix;
        (int prefixLength, int contentLength) = PeekLongPrefixAndContentLength(data, position, lengthOfLength);
        return prefixLength + contentLength;
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
            result = BitConverter.IsLittleEndian
                ? BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref firstElement))
                : Unsafe.ReadUnaligned<ushort>(ref firstElement);
        }
        else if (lengthOfLength == 3)
        {
            result = BitConverter.IsLittleEndian
                ? BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1)))
                    | (result << 16)
                : Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1))
                    | (result << 16);
        }
        else
        {
            result = BitConverter.IsLittleEndian
                ? BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref firstElement))
                : Unsafe.ReadUnaligned<int>(ref firstElement);
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
