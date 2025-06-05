// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

public static class Int64Extensions
{
    public static ReadOnlySpan<byte> ToBigEndianSpanWithoutLeadingZeros(this long value, out long buffer)
    {
        // Min 7 bytes as we still want a byte if the value is 0.
        var start = Math.Min(BitOperations.LeadingZeroCount((ulong)value) / sizeof(long), sizeof(long) - 1);
        // We create the span over the out value to ensure the span stack space remains valid.
        buffer = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        ReadOnlySpan<byte> span = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref buffer, 1));
        return span[start..];
    }

    public static byte[] ToBigEndianByteArrayWithoutLeadingZeros(this long value)
        => value.ToBigEndianSpanWithoutLeadingZeros(out _).ToArray();

    public static byte[] ToBigEndianByteArray(this ulong value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    public static byte[] ToBigEndianByteArray(this long value)
        => ToBigEndianByteArray((ulong)value);

    public static void WriteBigEndian(this long value, Span<byte> output)
    {
        BinaryPrimitives.WriteInt64BigEndian(output, value);
    }

    [SkipLocalsInit]
    public static string ToHexString(this long value, bool skipLeadingZeros)
    {
        if (value == 0L)
        {
            return Bytes.ZeroHexValue;
        }

        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes.ToHexString(true, skipLeadingZeros, false);
    }

    [SkipLocalsInit]
    public static string ToHexString(this ulong value, bool skipLeadingZeros)
    {
        if (value == 0UL)
        {
            return Bytes.ZeroHexValue;
        }

        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes.ToHexString(true, skipLeadingZeros, false);
    }

    [SkipLocalsInit]
    public static string ToHexString(this in UInt256 value, bool skipLeadingZeros)
    {
        if (skipLeadingZeros)
        {
            if (value.IsZero)
            {
                return Bytes.ZeroHexValue;
            }

            if (value.IsOne)
            {
                return "0x1";
            }
        }

        Span<byte> bytes = stackalloc byte[32];
        value.ToBigEndian(bytes);
        return bytes.ToHexString(true, skipLeadingZeros, false);
    }

    public static long ToLongFromBigEndianByteArrayWithoutLeadingZeros(this byte[]? bytes)
    {
        if (bytes is null)
        {
            return 0L;
        }

        long value = 0;
        int length = bytes.Length;

        for (int i = 0; i < length; i++)
        {
            value += (long)bytes[length - 1 - i] << 8 * i;
        }

        return value;
    }
}
