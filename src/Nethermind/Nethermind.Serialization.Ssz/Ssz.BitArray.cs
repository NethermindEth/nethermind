// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, BitArray value, ref int offset)
    {
        int byteLength = (value.Length + 7) / 8;
        EncodeVector(span.Slice(offset, byteLength), value);
        offset += byteLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeVector(Span<byte> span, BitArray value)
    {
        int byteLength = (value.Length + 7) / 8;
        byte[] bytes = new byte[byteLength];
        value.CopyTo(bytes, 0);
        Encode(span, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, BitArray value, ref int offset, ref int dynamicOffset)
    {
        int length = (value.Length + 8) / 8;
        Encode(span, dynamicOffset, ref offset);
        EncodeList(span.Slice(dynamicOffset, length), value);
        dynamicOffset += length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeList(Span<byte> span, BitArray value)
    {
        int byteLength = (value.Length + 8) / 8;
        byte[] bytes = new byte[byteLength];
        value.CopyTo(bytes, 0);
        bytes[byteLength - 1] |= (byte)(1 << (value.Length % 8));
        Encode(span, bytes);
    }

    public static BitArray DecodeBitvector(ReadOnlySpan<byte> span, int vectorLength)
    {
        BitArray value = new BitArray(span.ToArray());
        value.Length = vectorLength;
        return value;
    }

    public static BitArray DecodeBitlist(ReadOnlySpan<byte> span)
    {
        BitArray value = new BitArray(span.ToArray());
        int length = value.Length - 1;
        int lastByte = span[span.Length - 1];
        int mask = 0x80;
        while ((lastByte & mask) == 0 && mask > 0)
        {
            length--;
            mask = mask >> 1;
        }
        value.Length = length;
        return value;
    }
}
