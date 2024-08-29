// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// https://github.com/ethereum/eth2.0-specs/blob/dev/specs/simple-serialize.md#simpleserialize-ssz
/// </summary>
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
        const int expectedLength = 1;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeByte)} expects input of length {expectedLength} and received {span.Length}");
        }

        result = span[0];
    }

    public static void Decode(ReadOnlySpan<byte> span, out ushort result)
    {
        const int expectedLength = 2;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUShort)} expects input of length {expectedLength} and received {span.Length}");
        }

        result = BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out uint result)
    {
        const int expectedLength = 4;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInt)} expects input of length {expectedLength} and received {span.Length}");
        }

        result = BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out int result)
    {
        const int expectedLength = 4;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInt)} expects input of length {expectedLength} and received {span.Length}");
        }

        result = BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out ulong result)
    {
        const int expectedLength = 8;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(Decode)} expects input of length {expectedLength} and received {span.Length}");
        }

        result = BinaryPrimitives.ReadUInt64LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(ReadOnlySpan<byte> span, out long result)
    {
        const int expectedLength = 8;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(Decode)} expects input of length {expectedLength} and received {span.Length}");
        }

        result = BinaryPrimitives.ReadInt64LittleEndian(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out UInt128 result)
    {
        const int expectedLength = 16;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInt128)} expects input of length {expectedLength} and received {span.Length}");
        }

        ulong s0 = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
        ulong s1 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
        result = new UInt128(s0, s1);
    }

    public static void Decode(ReadOnlySpan<byte> span, out UInt256 value)
    {
        const int expectedLength = 32;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInt256)} expects input of length {expectedLength} and received {span.Length}");
        }

        value = new UInt256(span);
    }



    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<byte> result)
    {
        result = span;
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<ushort> result)
    {
        const int typeSize = 2;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUShorts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        result = MemoryMarshal.Cast<byte, ushort>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<short> result)
    {
        const int typeSize = 2;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUShorts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        result = MemoryMarshal.Cast<byte, short>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<uint> result)
    {
        const int typeSize = 4;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        result = MemoryMarshal.Cast<byte, uint>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<int> result)
    {
        const int typeSize = 4;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        result = MemoryMarshal.Cast<byte, int>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<ulong> result)
    {
        const int typeSize = 4;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        result = MemoryMarshal.Cast<byte, ulong>(span);
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<long> result)
    {
        const int typeSize = 4;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        result = MemoryMarshal.Cast<byte, long>(span);
    }
       
    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<UInt128> result)
    {
        const int typeSize = 16;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts128)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        UInt128[] array = new UInt128[span.Length / typeSize];

        for (int i = 0; i < span.Length / typeSize; i++)
        {
           Decode(span.Slice(i * typeSize, typeSize), out array[i]);
        }

        result = array;
    }

    public static void Decode(ReadOnlySpan<byte> span, out ReadOnlySpan<UInt256> result)
    {
        const int typeSize = 32;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts128)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        UInt256[] array = new UInt256[span.Length / typeSize];

        for (int i = 0; i < span.Length / typeSize; i++)
        {
            Decode(span.Slice(i * typeSize, typeSize), out array[i]);
        }

        result = array;
    }
}
