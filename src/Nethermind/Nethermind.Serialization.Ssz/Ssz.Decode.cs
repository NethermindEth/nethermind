// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out bool result)
    {
        result = span[0] != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out byte result)
    {
        ValidateLength(span, sizeof(byte));

        result = span[0];
    }

    public static void Decode(ReadOnlySpan<byte> span, out ushort result)
    {
        ValidateLength(span, sizeof(ushort));

        result = BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out uint result)
    {
        ValidateLength(span, sizeof(uint));

        result = BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out int result)
    {
        ValidateLength(span, sizeof(int));

        result = BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out ulong result)
    {
        ValidateLength(span, sizeof(ulong));

        result = BinaryPrimitives.ReadUInt64LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out long result)
    {
        ValidateLength(span, sizeof(long));

        result = BinaryPrimitives.ReadInt64LittleEndian(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out UInt128 result)
    {
        ValidateLength(span, 16);

        ulong s0 = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
        ulong s1 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
        result = new UInt128(s0, s1);
    }

    public static void Decode(ReadOnlySpan<byte> span, out UInt256 value)
    {
        ValidateLength(span, 32);

        value = new UInt256(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<byte> result)
    {
        result = span;
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<ushort> result)
    {
        ValidateArrayLength(span, sizeof(ushort));

        result = MemoryMarshal.Cast<byte, ushort>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<short> result)
    {
        ValidateArrayLength(span, sizeof(short));

        result = MemoryMarshal.Cast<byte, short>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<uint> result)
    {
        ValidateArrayLength(span, sizeof(uint));

        result = MemoryMarshal.Cast<byte, uint>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<int> result)
    {
        ValidateArrayLength(span, sizeof(int));

        result = MemoryMarshal.Cast<byte, int>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<ulong> result)
    {
        ValidateArrayLength(span, sizeof(ulong));

        result = MemoryMarshal.Cast<byte, ulong>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<long> result)
    {
        ValidateArrayLength(span, sizeof(long));

        result = MemoryMarshal.Cast<byte, long>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<UInt128> result)
    {
        int typeSize = 16;
        ValidateArrayLength(span, typeSize);

        UInt128[] array = new UInt128[span.Length / typeSize];

        for (int i = 0; i < span.Length / typeSize; i++)
        {
            Decode(span.Slice(i * typeSize, typeSize), out array[i]);
        }

        result = array;
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<UInt256> result)
    {
        int typeSize = 32;
        ValidateArrayLength(span, typeSize);

        UInt256[] array = new UInt256[span.Length / typeSize];

        for (int i = 0; i < span.Length / typeSize; i++)
        {
            Decode(span.Slice(i * typeSize, typeSize), out array[i]);
        }

        result = array;
    }

    public static void Decode(ReadOnlySpan<byte> span, int vectorLength, out BitArray vector)
    {
        BitArray value = new BitArray(span.ToArray())
        {
            Length = vectorLength
        };
        vector = value;
    }

    public static void Decode(ReadOnlySpan<byte> span, out BitArray list)
    {
        BitArray value = new BitArray(span.ToArray());
        int length = value.Length - 1;
        int lastByte = span[^1];
        int mask = 0x80;
        while ((lastByte & mask) == 0 && mask > 0)
        {
            length--;
            mask >>= 1;
        }
        value.Length = length;
        list = value;
    }

    private static void ValidateLength(ReadOnlySpan<byte> span, int expectedLength)
    {
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                 $"{nameof(DecodeByte)} expects input of length {expectedLength} and received {span.Length}");
        }
    }

    private static void ValidateArrayLength(ReadOnlySpan<byte> span, int itemLength)
    {
        if (span.Length % itemLength != 0)
        {
            throw new InvalidDataException(
                 $"{nameof(DecodeUShorts)} expects input in multiples of {itemLength} and received {span.Length}");
        }
    }
}
