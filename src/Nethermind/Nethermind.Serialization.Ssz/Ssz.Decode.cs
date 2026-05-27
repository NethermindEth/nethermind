// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    public static void Decode(ReadOnlySpan<byte> span, out UInt128 result)
    {
        ValidateLength(span, 16);

        ulong s0 = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
        ulong s1 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
        result = new UInt128(s1, s0);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<byte> result) => result = span;

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<bool> result)
    {
        ValidateBooleans(span);

        result = MemoryMarshal.Cast<byte, bool>(span);
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

    public static void Decode(ReadOnlySpan<byte> span, int vectorLength, out BitArray vector) =>
        vector = DecodeBitvector(span, vectorLength);

    public static void Decode(ReadOnlySpan<byte> span, out BitArray list) =>
        list = DecodeBitlist(span);

    public static BitArray DecodeBitvector(ReadOnlySpan<byte> span, int vectorLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorLength, nameof(vectorLength));

        int expectedBytes = (vectorLength + 7) / 8;
        if (span.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"Invalid bitvector: expected {expectedBytes} bytes for Bitvector[{vectorLength}] but got {span.Length}");
        }

        if (vectorLength % 8 != 0)
        {
            byte mask = (byte)(0xFF << (vectorLength % 8));
            if ((span[^1] & mask) != 0)
            {
                throw new InvalidDataException("Invalid bitvector: unused high bits are set");
            }
        }

        BitArray value = new(span.ToArray());
        value.Length = vectorLength;
        return value;
    }

    public static BitArray DecodeBitlist(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
        {
            throw new InvalidDataException("Invalid bitlist: empty data (missing sentinel bit)");
        }

        if (span[^1] == 0)
        {
            throw new InvalidDataException("Invalid bitlist: last byte is zero (missing sentinel bit)");
        }

        BitArray value = new(span.ToArray());
        int length = value.Length - 1;
        int lastByte = span[^1];
        int mask = 0x80;
        while ((lastByte & mask) == 0 && mask > 0)
        {
            length--;
            mask >>= 1;
        }
        value.Length = length;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ToContiguous(ReadOnlySequence<byte> data, Span<byte> stackBuffer)
    {
        if (data.IsSingleSegment) return data.FirstSpan;
        data.CopyTo(stackBuffer);
        return stackBuffer;
    }

    public static void Decode(ReadOnlySequence<byte> data, out UInt128 result)
    {
        ValidateSequenceLength<UInt128>(data, 16);

        Span<byte> stack = stackalloc byte[16];
        Decode(ToContiguous(data, stack), out result);
    }

    /// <summary>
    /// Materializes a (typically variable-length) byte region. Single-segment is zero-copy
    /// up to <see cref="byte"/>[] allocation; multi-segment performs one consolidated copy.
    /// </summary>
    public static byte[] DecodeBytes(ReadOnlySequence<byte> data) =>
        data.IsEmpty ? [] : data.ToArray();

    public static void Decode(ReadOnlySequence<byte> data, int vectorLength, out BitArray vector)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorLength, nameof(vectorLength));

        int byteLength = (vectorLength + 7) / 8;
        if (data.Length != byteLength)
        {
            throw new InvalidDataException(
                $"Invalid bitvector: expected {byteLength} bytes for Bitvector[{vectorLength}] but got {data.Length}");
        }

        if (data.IsSingleSegment)
        {
            vector = DecodeBitvector(data.FirstSpan, vectorLength);
            return;
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            data.CopyTo(rented.AsSpan(0, byteLength));
            vector = DecodeBitvector(rented.AsSpan(0, byteLength), vectorLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static void Decode(ReadOnlySequence<byte> data, out BitArray list)
    {
        if (data.IsSingleSegment)
        {
            list = DecodeBitlist(data.FirstSpan);
            return;
        }
        int byteLength = (int)data.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteLength);
        try
        {
            data.CopyTo(rented.AsSpan(0, byteLength));
            list = DecodeBitlist(rented.AsSpan(0, byteLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ValidateSequenceLength<T>(ReadOnlySequence<byte> data, int expectedLength)
    {
        if (data.Length != expectedLength)
        {
            Type type = typeof(T);
            throw new InvalidDataException(
                $"Invalid source length in SSZ decoding of {type.Name}. Source length is {data.Length} and expected length is {expectedLength}.");
        }
    }

    private static void ValidateLength(ReadOnlySpan<byte> span, int expectedLength)
    {
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                 $"SSZ decode expects input of length {expectedLength} and received {span.Length}");
        }
    }

    private static void ValidateArrayLength(ReadOnlySpan<byte> span, int itemLength)
    {
        if (span.Length % itemLength != 0)
        {
            throw new InvalidDataException(
                 $"SSZ decode expects input in multiples of {itemLength} and received {span.Length}");
        }
    }

    private static void ValidateBooleans(ReadOnlySpan<byte> span)
    {
        foreach (byte value in span)
        {
            if (value > 1)
            {
                throw new InvalidDataException("SSZ boolean value must be 0 or 1.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowSourceLength<T>(int sourceLength, int expectedLength)
    {
        Type type = typeof(T);
        throw new InvalidDataException(
            $"Invalid source length in SSZ decoding of {type.Name}. Source length is {sourceLength} and expected length is {expectedLength}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowInvalidSourceArrayLength<T>(int sourceLength, int expectedItemLength)
    {
        Type type = typeof(T);
        throw new InvalidDataException(
            $"Invalid source length in SSZ decoding of {type.Name}. Source length is {sourceLength} and expected length is a multiple of {expectedItemLength}.");
    }
}
