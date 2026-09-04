// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Shared helper methods for value-based RLP processing.
/// </summary>
internal static class RlpHelpers
{
    public const int SmallPrefixBarrier = 56;

    /// <summary>RLP prefix byte introducing a 32-byte string, i.e. a hash.</summary>
    public const int KeccakRlpPrefix = Rlp.EmptyByteArrayByte + Hash256.Size;

    /// <summary>RLP prefix byte introducing a 20-byte string, i.e. an address.</summary>
    public const int AddressRlpPrefix = Rlp.EmptyByteArrayByte + Address.Size;

    internal static ReadOnlySpan<byte> SingleBytes => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127];

    internal static readonly byte[][] SingleByteArrays = CreateSingleByteArrays();

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

    private static byte[][] CreateSingleByteArrays()
    {
        byte[][] arrays = new byte[128][];
        for (int i = 0; i < arrays.Length; i++)
        {
            arrays[i] = [(byte)i];
        }

        return arrays;
    }

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

        if (position + 1 + lengthOfLength > data.Length)
        {
            ThrowRlpDataTruncated();
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

        if (contentLength > data.Length - position - (1 + lengthOfLength))
        {
            ThrowRlpDataTruncated();
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
    /// This is shared by RLP byte-array decoders.
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
            result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref firstElement));
        }
        else if (lengthOfLength == 3)
        {
            result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1)))
                | (result << 16);
        }
        else
        {
            result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref firstElement));
        }

        return result;

        [DoesNotReturn]
        static void ThrowInvalidData() => throw new RlpException("Length starts with 0");
    }

    // The overloads below take the cursor by value and return the advanced one, so a chain of them keeps
    // it in a register. Reaching it through RlpReader instead costs a 4-byte field round-trip per call,
    // which ZisK charges 122 to read and 193 to write.

    /// <summary>Advances past the prefix of the item at <paramref name="position"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SkipLength(ReadOnlySpan<byte> data, int position)
        => position + GetPrefixLength(data[position]);

    /// <summary>Advances past <paramref name="count"/> whole items.</summary>
    public static int SkipItems(ReadOnlySpan<byte> data, int position, int count)
    {
        for (int i = 0; i < count; i++)
        {
            position += PeekNextRlpLength(data, position);
        }

        return position;
    }

    /// <summary>Reads a multi-byte length field.</summary>
    /// <returns>The position past the field.</returns>
    public static int DeserializeLength(ReadOnlySpan<byte> data, int position, int lengthOfLength, out int length)
    {
        if (lengthOfLength == 0 || (uint)lengthOfLength > 4)
        {
            ThrowInvalidLength(lengthOfLength);
        }

        length = DeserializeLengthRef(ref MemoryMarshal.GetReference(data.Slice(position, lengthOfLength)), lengthOfLength);
        return position + lengthOfLength;
    }

    /// <summary>Reads a sequence header, yielding its content length.</summary>
    /// <returns>The position of the first item in the sequence.</returns>
    public static int ReadSequenceLength(ReadOnlySpan<byte> data, int position, out int contentLength)
    {
        int prefix = data[position++];
        if (prefix < ListOffset)
        {
            ThrowUnexpectedPrefix(prefix);
        }

        if (prefix <= ShortListMaxPrefix)
        {
            contentLength = prefix - ListOffset;
            return position;
        }

        position = DeserializeLength(data, position, prefix - ShortListMaxPrefix, out contentLength);
        if (contentLength < SmallPrefixBarrier)
        {
            ThrowUnexpectedLength(contentLength);
        }

        return position;
    }

    /// <summary>Reads the prefix of the item at <paramref name="position"/>, yielding both lengths.</summary>
    /// <returns>The position of the item's content.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadPrefixAndContentLength(
        ReadOnlySpan<byte> data, int position, out int prefixLength, out int contentLength)
    {
        (prefixLength, contentLength) = PeekPrefixAndContentLength(data, position);
        return position + Math.Max(prefixLength, 1);
    }

    /// <summary>Decodes a byte string, yielding a span over <paramref name="data"/>.</summary>
    /// <returns>The position past the string.</returns>
    public static int DecodeByteArraySpan(
        ReadOnlySpan<byte> data, int position, RlpLimit? limit, int size, out ReadOnlySpan<byte> value)
    {
        int start = position;
        int prefix = data[position++];
        ReadOnlySpan<byte> singles = SingleBytes;
        if ((uint)prefix < (uint)singles.Length)
        {
            Rlp.GuardSize(actual: 1, expected: size);
            value = singles.Slice(prefix, 1);
            return position;
        }

        if (prefix is Rlp.EmptyByteArrayByte)
        {
            Rlp.GuardSize(actual: 0, expected: size);
            value = default;
            return position;
        }

        if (prefix <= ShortStringMaxPrefix)
        {
            int length = prefix - ShortStringOffset;
            Rlp.GuardLimit(length, data.Length - position, limit);
            Rlp.GuardSize(actual: length, expected: size);

            value = data.Slice(position, length);
            if (length == 1 && value[0] < 128)
            {
                ThrowNonCanonicalInteger(start);
            }

            return position + length;
        }

        return DecodeLargerByteArraySpan(data, position, prefix, limit, size, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DecodeLargerByteArraySpan(
        ReadOnlySpan<byte> data, int position, int prefix, RlpLimit? limit, int size, out ReadOnlySpan<byte> value)
    {
        if (prefix < ListOffset)
        {
            int lengthOfLength = prefix - ShortStringMaxPrefix;
            if (lengthOfLength > 4)
            {
                ThrowSequenceLengthTooLong();
            }

            position = DeserializeLength(data, position, lengthOfLength, out int length);
            if (length < SmallPrefixBarrier)
            {
                ThrowUnexpectedLength(length);
            }

            Rlp.GuardSize(actual: length, expected: size);
            Rlp.GuardLimit(length, data.Length - position, limit);
            value = data.Slice(position, length);
            return position + length;
        }

        ThrowUnexpectedPrefix(prefix);
        value = default;
        return position;
    }

    /// <summary>Decodes a big-endian unsigned integer of up to 8 bytes.</summary>
    /// <returns>The position past the integer.</returns>
    public static int DecodeULong(ReadOnlySpan<byte> data, int position, out ulong value)
    {
        int start = position;
        int prefix = data[position++];

        switch (prefix)
        {
            case 0:
                value = ThrowNonCanonicalInteger(start);
                return position;
            case < 128:
                value = (ulong)prefix;
                return position;
            case 128:
                value = 0;
                return position;
        }

        int length = prefix - 128;
        if (length > 8)
        {
            ThrowUnexpectedIntegerLength(start, length);
        }

        ulong result = 0ul;
        for (int i = 8; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= data[position + length - i];
                if (result == 0)
                {
                    ThrowNonCanonicalInteger(start);
                }
            }
        }

        if (result < 128)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = result;
        return position + length;
    }

    /// <inheritdoc cref="DecodeULong"/>
    public static int DecodeUInt(ReadOnlySpan<byte> data, int position, out uint value)
    {
        int start = position;
        int prefix = data[position++];

        switch (prefix)
        {
            case 0:
                value = ThrowNonCanonicalInteger(start);
                return position;
            case < 128:
                value = (uint)prefix;
                return position;
            case 128:
                value = 0u;
                return position;
        }

        int length = prefix - 128;
        if (length > 4)
        {
            ThrowUnexpectedIntegerLength(start, length);
        }

        uint result = 0;
        for (int i = 4; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= data[position + length - i];
                if (result == 0)
                {
                    ThrowNonCanonicalInteger(start);
                }
            }
        }

        if (result < 128)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = result;
        return position + length;
    }

    /// <inheritdoc cref="DecodeULong"/>
    public static int DecodeUShort(ReadOnlySpan<byte> data, int position, out ushort value)
    {
        int start = position;
        int prefix = data[position++];

        switch (prefix)
        {
            case 0:
                ThrowNonCanonicalInteger(start);
                value = 0;
                return position;
            case < 128:
                value = (ushort)prefix;
                return position;
            case 128:
                value = 0;
                return position;
        }

        int length = prefix - 128;
        if (length > 2)
        {
            ThrowUnexpectedIntegerLength(start, length);
        }

        ushort result = 0;
        for (int i = 2; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= data[position + length - i];
                if (result == 0)
                {
                    ThrowNonCanonicalInteger(start);
                }
            }
        }

        if (result < 128)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = result;
        return position + length;
    }

    /// <summary>Decodes a single byte value.</summary>
    /// <returns>The position past the value.</returns>
    public static int DecodeByte(ReadOnlySpan<byte> data, int position, out byte value)
    {
        byte byteValue = data[position];
        switch (byteValue)
        {
            case 0:
                ThrowNonCanonicalInteger(position);
                value = 0;
                return position;
            case < 128:
                value = byteValue;
                return position + 1;
            case 128:
                value = 0;
                return position + 1;
            case 129 when data[position + 1] < 128:
                ThrowNonCanonicalInteger(position);
                value = 0;
                return position;
            case 129:
                value = data[position + 1];
                return position + 2;
            default:
                ThrowUnexpectedByteValue(position, byteValue);
                value = 0;
                return position;
        }
    }

    /// <summary>Decodes a signed integer that must not be negative.</summary>
    /// <returns>The position past the integer.</returns>
    public static int DecodePositiveInt(ReadOnlySpan<byte> data, int position, out int value)
    {
        int start = position;
        position = DecodeUInt(data, position, out uint unsigned);
        value = (int)unsigned;
        if (value < 0)
        {
            ThrowNegativeInteger(start, value);
        }

        return position;
    }

    /// <inheritdoc cref="DecodePositiveInt"/>
    public static int DecodePositiveLong(ReadOnlySpan<byte> data, int position, out long value)
    {
        int start = position;
        position = DecodeULong(data, position, out ulong unsigned);
        value = (long)unsigned;
        if (value < 0)
        {
            ThrowNegativeInteger(start, value);
        }

        return position;
    }

    /// <summary>Decodes a big-endian unsigned integer of up to 32 bytes.</summary>
    /// <param name="length">Required byte length, or -1 to accept any canonical encoding.</param>
    /// <returns>The position past the integer.</returns>
    public static int DecodeUInt256(ReadOnlySpan<byte> data, int position, int length, out UInt256 value)
    {
        int start = position;
        if (data[position] == 0)
        {
            ThrowNonCanonicalInteger(start);
        }

        position = DecodeByteArraySpan(data, position, RlpLimit.L32, -1, out ReadOnlySpan<byte> byteSpan);
        if (byteSpan.Length > 32)
        {
            ThrowUnexpectedIntegerLength(start, byteSpan.Length);
        }

        if (length == -1)
        {
            if (byteSpan.Length > 1 && byteSpan[0] == 0)
            {
                ThrowNonCanonicalInteger(start);
            }
        }
        else if (byteSpan.Length != length)
        {
            ThrowInvalidLength(byteSpan.Length, length);
        }

        value = new UInt256(byteSpan, true);
        return position;
    }

    /// <summary>Decodes a big-endian unsigned integer into a right-aligned 32-byte word.</summary>
    /// <returns>The position past the integer.</returns>
    public static int DecodeEvmWord(ReadOnlySpan<byte> data, int position, out EvmWord value)
    {
        int start = position;
        if (data[position] == 0)
        {
            ThrowNonCanonicalInteger(start);
        }

        position = DecodeByteArraySpan(data, position, RlpLimit.L32, -1, out ReadOnlySpan<byte> byteSpan);
        if (byteSpan.Length > 32)
        {
            ThrowUnexpectedIntegerLength(start, byteSpan.Length);
        }

        if (byteSpan.Length > 1 && byteSpan[0] == 0)
        {
            ThrowNonCanonicalInteger(start);
        }

        value = default;
        Span<byte> dest = MemoryMarshal.CreateSpan(ref Unsafe.As<EvmWord, byte>(ref value), 32);
        byteSpan.CopyTo(dest.Slice(32 - byteSpan.Length));
        return position;
    }

    /// <summary>Reads the fixed-size prefix introducing a hash.</summary>
    /// <returns>The position past the prefix; <paramref name="hasValue"/> is false for an RLP null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadKeccakPrefix(ReadOnlySpan<byte> data, int position, bool allowNull, out bool hasValue)
    {
        int prefix = data[position++];
        hasValue = prefix == KeccakRlpPrefix;
        if (!hasValue && !(allowNull && prefix == Rlp.EmptyByteArrayByte))
        {
            ThrowKeccakDecode(prefix, position, data.Length);
        }

        return position;
    }

    /// <inheritdoc cref="ReadKeccakPrefix"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadAddressPrefix(ReadOnlySpan<byte> data, int position, bool allowNull, out bool hasValue)
    {
        int prefix = data[position++];
        hasValue = prefix == AddressRlpPrefix;
        if (!hasValue && !(allowNull && prefix == Rlp.EmptyByteArrayByte))
        {
            ThrowAddressDecode(prefix, position, data.Length);
        }

        return position;
    }

    /// <summary>Decodes a 32-byte hash, or an RLP null.</summary>
    /// <returns>The position past the item.</returns>
    public static int DecodeKeccakOrNull(ReadOnlySpan<byte> data, int position, out Hash256? keccak)
    {
        position = ReadKeccakPrefix(data, position, allowNull: true, out bool hasValue);
        if (!hasValue)
        {
            keccak = null;
            return position;
        }

        keccak = InternKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <inheritdoc cref="DecodeKeccakOrNull"/>
    public static int DecodeValueKeccakOrNull(ReadOnlySpan<byte> data, int position, out ValueHash256? keccak)
    {
        position = ReadKeccakPrefix(data, position, allowNull: true, out bool hasValue);
        if (!hasValue)
        {
            keccak = null;
            return position;
        }

        keccak = InternValueKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <summary>Decodes a 32-byte hash without interning, reporting an RLP null instead of throwing.</summary>
    /// <returns>The position past the item.</returns>
    public static int TryDecodeValueKeccak(ReadOnlySpan<byte> data, int position, out ValueHash256 keccak, out bool hasValue)
    {
        position = ReadKeccakPrefix(data, position, allowNull: true, out hasValue);
        if (!hasValue)
        {
            Unsafe.SkipInit(out keccak);
            return position;
        }

        keccak = new ValueHash256(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <summary>Decodes a 20-byte address, or an RLP null.</summary>
    /// <returns>The position past the item.</returns>
    public static int DecodeAddress(ReadOnlySpan<byte> data, int position, bool allowNull, out Address? address)
    {
        position = ReadAddressPrefix(data, position, allowNull, out bool hasValue);
        if (!hasValue)
        {
            address = null;
            return position;
        }

        address = new Address(data.Slice(position, Address.Size));
        return position + Address.Size;
    }

    /// <summary>Yields the span holding a bloom, tolerating the legacy sequence form.</summary>
    /// <remarks>
    /// Some nodes send receipt blooms wrapped in a sequence rather than as a plain 256-byte string;
    /// see https://github.com/NethermindEth/nethermind/issues/113. An empty span means an RLP null.
    /// </remarks>
    /// <returns>The position past the item.</returns>
    public static int DecodeBloomSpan(ReadOnlySpan<byte> data, int position, out ReadOnlySpan<byte> bloomBytes)
    {
        if (data[position] == 249)
        {
            position += 5; // skip 249 1 2 129 127 and read 256 bytes
            bloomBytes = data.Slice(position, Bloom.ByteLength);
            return position + Bloom.ByteLength;
        }

        return DecodeByteArraySpan(data, position, RlpLimit.Bloom, -1, out bloomBytes);
    }

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowAddressDecode(int prefix, int position, int dataLength)
        => throw new RlpException(
            $"Unexpected RLP prefix of {prefix} when decoding {nameof(Address)} at position {position} in the message of length {dataLength}.");

    /// <summary>Decodes a byte string into an array, reusing the shared single-byte arrays.</summary>
    /// <returns>The position past the string.</returns>
    public static int DecodeByteArray(
        ReadOnlySpan<byte> data, int position, RlpLimit? limit, int size, out byte[] value)
    {
        position = DecodeByteArraySpan(data, position, limit, size, out ReadOnlySpan<byte> span);
        if (span.Length == 0)
        {
            value = [];
            return position;
        }

        if (span.Length == 1)
        {
            int single = span[0];
            byte[][] arrays = SingleByteArrays;
            if ((uint)single < (uint)arrays.Length)
            {
                value = arrays[single];
                return position;
            }
        }

        value = span.ToArray();
        return position;
    }

    /// <summary>Decodes a boolean.</summary>
    /// <returns>The position past the value.</returns>
    public static int DecodeBool(ReadOnlySpan<byte> data, int position, out bool value)
    {
        byte prefix = data[position++];
        switch (prefix)
        {
            case 1:
                value = true;
                return position;
            case 128:
                value = false;
                return position;
            default:
                ThrowUnexpectedBoolValue(prefix);
                value = false;
                return position;
        }
    }

    /// <summary>Decodes a 32-byte hash that must be present.</summary>
    /// <returns>The position past the hash.</returns>
    public static int DecodeKeccak(ReadOnlySpan<byte> data, int position, out Hash256 keccak)
    {
        int prefix = data[position++];
        if (prefix != KeccakRlpPrefix)
        {
            ThrowKeccakDecode(prefix, position, data.Length);
        }

        keccak = InternKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <inheritdoc cref="DecodeKeccak"/>
    /// <remarks>An RLP null throws, matching <see cref="RlpReader.DecodeValueKeccakNonNull"/>.</remarks>
    public static int DecodeValueKeccakNonNull(ReadOnlySpan<byte> data, int position, out ValueHash256 keccak)
    {
        int prefix = data[position++];
        if (prefix != KeccakRlpPrefix)
        {
            if (prefix == Rlp.EmptyByteArrayByte)
            {
                ThrowNullDecodedValue<ValueHash256>();
            }

            ThrowKeccakDecode(prefix, position, data.Length);
        }

        keccak = InternValueKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    /// <summary>Returns the shared instance for the two hashes that dominate account payloads.</summary>
    public static Hash256 InternKeccak(ReadOnlySpan<byte> span)
        => span.SequenceEqual(Keccak.OfAnEmptyString.Bytes) ? Keccak.OfAnEmptyString
            : span.SequenceEqual(Keccak.EmptyTreeHash.Bytes) ? Keccak.EmptyTreeHash
            : new Hash256(span);

    /// <inheritdoc cref="InternKeccak"/>
    public static ValueHash256 InternValueKeccak(ReadOnlySpan<byte> span)
        => span.SequenceEqual(Keccak.OfAnEmptyString.Bytes) ? Keccak.OfAnEmptyString.ValueHash256
            : span.SequenceEqual(Keccak.EmptyTreeHash.Bytes) ? Keccak.EmptyTreeHash.ValueHash256
            : new ValueHash256(span);

    [DoesNotReturn, StackTraceHidden]
    public static T ThrowNullDecodedValue<T>() => throw new RlpException($"{typeof(T).Name} decoded as null");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowKeccakDecode(int prefix, int position, int dataLength)
        => throw new DecodeKeccakRlpException(prefix, position, dataLength);

    // Used to avoid allocating detailed error strings on receipt fallback decode paths.
    private sealed class DecodeKeccakRlpException(int prefix, int position, int dataLength) : RlpException(string.Empty)
    {
        private string? _message;

        public override string Message => _message ??= ConstructMessage();

        private string ConstructMessage() => $"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {position} in the message of length {dataLength}.";
    }

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedBoolValue(byte value)
        => throw new RlpException($"Unexpected value for a boolean: {value}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedByteValue(int position, int value)
        => throw new RlpException($"Unexpected byte value {value} at {position}");

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
    public static void ThrowRlpDataTruncated()
        => throw new RlpException("RLP data is truncated: not enough bytes for the declared length prefix");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedLength(int length)
        => throw new RlpException($"Expected length greater than or equal to 56 and was {length}");

    [DoesNotReturn, StackTraceHidden]
    public static uint ThrowNonCanonicalInteger(int position)
        => throw new RlpException($"Non-canonical integer at position {position}");

    [DoesNotReturn, StackTraceHidden]
    public static ulong ThrowNonceTooWide(int position)
        => throw new RlpException($"NonceTooWide: Transaction nonce exceeds uint64 at position {position}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowUnexpectedIntegerLength(int position, int length)
        => throw new RlpException($"Unexpected length of integer value {length} at position {position}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowNegativeInteger(int position, long value)
        => throw new RlpException($"Expected non-negative integer and was {value} at position {position}");

    [DoesNotReturn, StackTraceHidden]
    public static void ThrowNullArrayElement(int index)
        => throw new RlpException($"Unexpected null array element at index {index}");
}
