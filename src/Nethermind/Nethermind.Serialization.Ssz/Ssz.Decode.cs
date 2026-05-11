// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
        ValidateLength(span, sizeof(bool));

        result = span[0] switch
        {
            0 => false,
            1 => true,
            var x => throw new InvalidDataException($"SSZ bool must be 0 or 1, got {x}")
        };
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
        result = new UInt128(s1, s0);
    }

    public static void Decode(ReadOnlySpan<byte> span, out UInt256 value)
    {
        ValidateLength(span, 32);

        value = new UInt256(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<byte> result) => result = span;

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

    public static void Decode(ReadOnlySpan<byte> span, int vectorLength, out BitArray vector) =>
        vector = DecodeBitvector(span, vectorLength);

    public static void Decode(ReadOnlySpan<byte> span, out BitArray list) =>
        list = DecodeBitlist(span);

    // Sequence-aware overloads: enable zero-copy decoding from a PipeReader's
    // ReadOnlySequence<byte>. Multi-segment fixed primitives copy ≤32 bytes onto the stack;
    // single-segment hits the span fast path.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ToContiguous(ReadOnlySequence<byte> data, Span<byte> stackBuffer)
    {
        if (data.IsSingleSegment) return data.FirstSpan;
        data.CopyTo(stackBuffer);
        return stackBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySequence<byte> data, out bool result)
    {
        // Defer to the span overload — it validates the byte is 0 or 1 (per SSZ spec)
        // and our sequence path must not silently disagree on invalid input.
        Span<byte> stack = stackalloc byte[sizeof(bool)];
        Decode(ToContiguous(data, stack), out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySequence<byte> data, out byte result)
    {
        Span<byte> stack = stackalloc byte[sizeof(byte)];
        Decode(ToContiguous(data, stack), out result);
    }

    public static void Decode(ReadOnlySequence<byte> data, out ushort result)
    {
        Span<byte> stack = stackalloc byte[sizeof(ushort)];
        Decode(ToContiguous(data, stack), out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySequence<byte> data, out uint result)
    {
        Span<byte> stack = stackalloc byte[sizeof(uint)];
        Decode(ToContiguous(data, stack), out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySequence<byte> data, out int result)
    {
        Span<byte> stack = stackalloc byte[sizeof(int)];
        Decode(ToContiguous(data, stack), out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySequence<byte> data, out ulong result)
    {
        Span<byte> stack = stackalloc byte[sizeof(ulong)];
        Decode(ToContiguous(data, stack), out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySequence<byte> data, out long result)
    {
        Span<byte> stack = stackalloc byte[sizeof(long)];
        Decode(ToContiguous(data, stack), out result);
    }

    public static void Decode(ReadOnlySequence<byte> data, out UInt128 result)
    {
        Span<byte> stack = stackalloc byte[16];
        Decode(ToContiguous(data, stack), out result);
    }

    public static void Decode(ReadOnlySequence<byte> data, out UInt256 value)
    {
        Span<byte> stack = stackalloc byte[32];
        Decode(ToContiguous(data, stack), out value);
    }

    /// <summary>
    /// Materializes a (typically variable-length) byte region. Single-segment is zero-copy
    /// up to <see cref="byte"/>[] allocation; multi-segment performs one consolidated copy.
    /// </summary>
    public static byte[] DecodeBytes(ReadOnlySequence<byte> data) =>
        data.IsEmpty ? [] : data.ToArray();

    public static void Decode(ReadOnlySequence<byte> data, int vectorLength, out BitArray vector)
    {
        int byteLength = (vectorLength + 7) / 8;
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

    private static void ValidateLength(ReadOnlySpan<byte> span, int expectedLength)
    {
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                 $"SSZ decode expects input of length {expectedLength} and received {span.Length}");
        }
    }

    private static void ValidateLength(long actualLength, int expectedLength)
    {
        if (actualLength != expectedLength)
        {
            throw new InvalidDataException(
                 $"SSZ decode expects input of length {expectedLength} and received {actualLength}");
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
}
