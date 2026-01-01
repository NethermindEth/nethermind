// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions;

public static class UInt64Extensions
{
    public static void WriteBigEndian(this ulong value, Span<byte> output)
    {
        BinaryPrimitives.WriteUInt64BigEndian(output, value);
    }

    public static ReadOnlySpan<byte> ToBigEndianSpanWithoutLeadingZeros(this ulong value, out ulong buffer)
    {
        // Min 7 bytes as we still want a byte if the value is 0.
        int start = Math.Min(BitOperations.LeadingZeroCount(value) / sizeof(ulong), sizeof(ulong) - 1);
        buffer = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        ReadOnlySpan<byte> span = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref buffer, 1));
        return span[start..];
    }

    public static byte[] ToBigEndianByteArrayWithoutLeadingZeros(this ulong value)
        => value.ToBigEndianSpanWithoutLeadingZeros(out _).ToArray();

    public static ulong ToULongFromBigEndianByteArrayWithoutLeadingZeros(this byte[]? bytes) =>
        ToULongFromBigEndianByteArrayWithoutLeadingZeros(bytes.AsSpan());

    public static ulong ToULongFromBigEndianByteArrayWithoutLeadingZeros(this ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0L;
        }

        ulong value = 0;
        int length = bytes.Length;

        for (int i = 0; i < length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }
}
