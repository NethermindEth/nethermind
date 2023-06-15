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
    private static void Encode(Span<byte> span, byte[] value, ref int offset)
    {
        Encode(span.Slice(offset, value.Length), value);
        offset += value.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, int value, ref int offset)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), value);
        offset += sizeof(int);
    }

    private static void Encode(Span<byte> span, uint value, ref int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, sizeof(uint)), value);
        offset += sizeof(uint);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, ulong value, ref int offset)
    {
        Encode(span.Slice(offset, sizeof(ulong)), value);
        offset += sizeof(ulong);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, UInt256 value, ref int offset)
    {
        Encode(span.Slice(offset, 32), value);
        offset += 32;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> span, bool value, ref int offset)
    {
        Encode(span.Slice(offset, 1), value);
        offset++;
    }

    private static bool DecodeBool(Span<byte> span, ref int offset)
    {
        return span[offset++] == 1;
    }

    public static void Encode(Span<byte> span, byte value)
    {
        span[0] = value;
    }

    private static void Encode(Span<byte> span, byte value, ref int offset)
    {
        span[offset++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, int value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span[..8], (ulong)(value & ulong.MaxValue));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8, 8), (ulong)(value >> 64));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, UInt256 value)
    {
        value.ToLittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, bool value)
    {
        span[0] = Encode(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Encode(bool value)
    {
        return value ? (byte)1 : (byte)0;
    }

    public static void Encode(Span<byte> span, Span<bool> value)
    {
        if (span.Length != value.Length)
        {
            ThrowTargetLength<bool[]>(span.Length, value.Length);
        }

        for (int i = 0; i < value.Length; i++)
        {
            span[i] = Encode(value[i]);
        }
    }

    public static void Encode(Span<byte> span, UInt256[] value)
    {
        const int typeSize = 32;
        if (span.Length != value.Length * typeSize)
        {
            ThrowTargetLength<UInt256[]>(span.Length, value.Length);
        }

        for (int i = 0; i < value.Length; i++)
        {
            Encode(span.Slice(i * typeSize, typeSize), value[i]);
        }
    }

    public static void Encode(Span<byte> span, UInt128[] value)
    {
        const int typeSize = 16;
        if (span.Length != value.Length * typeSize)
        {
            ThrowTargetLength<UInt128[]>(span.Length, value.Length);
        }

        for (int i = 0; i < value.Length; i++)
        {
            Encode(span.Slice(i * typeSize, typeSize), value[i]);
        }
    }

    public static void Encode(Span<byte> span, Span<ulong> value)
    {
        const int typeSize = 8;
        if (span.Length != value.Length * typeSize)
        {
            ThrowTargetLength<ulong[]>(span.Length, value.Length);
        }

        MemoryMarshal.Cast<ulong, byte>(value).CopyTo(span);
    }

    public static void Encode(Span<byte> span, Span<uint> value)
    {
        const int typeSize = 4;
        if (span.Length != value.Length * typeSize)
        {
            ThrowTargetLength<uint[]>(span.Length, value.Length);
        }

        MemoryMarshal.Cast<uint, byte>(value).CopyTo(span);
    }

    public static void Encode(Span<byte> span, Span<ushort> value)
    {
        const int typeSize = 2;
        if (span.Length != value.Length * typeSize)
        {
            ThrowTargetLength<ushort[]>(span.Length, value.Length);
        }

        MemoryMarshal.Cast<ushort, byte>(value).CopyTo(span);
    }

    private static void Encode(Span<byte> span, Span<byte> value, ref int offset, ref int dynamicOffset)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, VarOffsetSize), dynamicOffset);
        offset += VarOffsetSize;
        value.CopyTo(span.Slice(dynamicOffset, value.Length));
        dynamicOffset += value.Length;
    }

    public static void Encode(Span<byte> span, ReadOnlySpan<byte> value)
    {
        const int typeSize = 1;
        if (span.Length < value.Length * typeSize)
        {
            ThrowTargetLength<byte[]>(span.Length, value.Length);
        }

        value.CopyTo(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DecodeBool(Span<byte> span)
    {
        return span[0] != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte DecodeByte(ReadOnlySpan<byte> span)
    {
        const int expectedLength = 1;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeByte)} expects input of length {expectedLength} and received {span.Length}");
        }

        return span[0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte DecodeByte(ReadOnlySpan<byte> span, ref int offset)
    {
        return span[offset++];
    }

    public static ushort DecodeUShort(Span<byte> span)
    {
        const int expectedLength = 2;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUShort)} expects input of length {expectedLength} and received {span.Length}");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DecodeUInt(ReadOnlySpan<byte> span)
    {
        const int expectedLength = 4;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInt)} expects input of length {expectedLength} and received {span.Length}");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong DecodeULong(ReadOnlySpan<byte> span)
    {
        const int expectedLength = 8;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeULong)} expects input of length {expectedLength} and received {span.Length}");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DecodeULong(ReadOnlySpan<byte> span, ref int offset)
    {
        ulong result = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        return result;
    }

    public static UInt128 DecodeUInt128(Span<byte> span)
    {
        const int expectedLength = 16;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInt128)} expects input of length {expectedLength} and received {span.Length}");
        }

        ulong s0 = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
        ulong s1 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
        return new UInt128(s0, s1);
    }

    public static UInt256 DecodeUInt256(ReadOnlySpan<byte> span)
    {
        const int expectedLength = 32;
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInt256)} expects input of length {expectedLength} and received {span.Length}");
        }

        return new UInt256(span);
    }

    public static UInt256 DecodeUInt256(ReadOnlySpan<byte> span, ref int offset)
    {
        UInt256 result = new(span.Slice(offset, 32));
        offset += 32;
        return result;
    }

    public static UInt256[] DecodeUInts256(Span<byte> span)
    {
        const int typeSize = 32;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts256)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        UInt256[] result = new UInt256[span.Length / typeSize];
        for (int i = 0; i < span.Length / typeSize; i++)
        {
            result[i] = DecodeUInt256(span.Slice(i * typeSize, typeSize));
        }

        return result;
    }

    private static byte[][] DecodeBytesArrays(ReadOnlySpan<byte> span, int itemsCount, int itemLength, ref int offset)
    {
        byte[][] result = new byte[itemsCount][];

        for (int i = 0; i < itemsCount; i++)
        {
            result[i] = span.Slice(offset, itemLength).ToArray();
            offset += itemLength;
        }

        return result;
    }

    public static UInt128[] DecodeUInts128(Span<byte> span)
    {
        const int typeSize = 16;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts128)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        UInt128[] result = new UInt128[span.Length / typeSize];
        for (int i = 0; i < span.Length / typeSize; i++)
        {
            result[i] = DecodeUInt128(span.Slice(i * typeSize, typeSize));
        }

        return result;
    }

    public static Span<ulong> DecodeULongs(Span<byte> span)
    {
        const int typeSize = 8;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeULongs)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        return MemoryMarshal.Cast<byte, ulong>(span);
    }

    public static Span<uint> DecodeUInts(Span<byte> span)
    {
        const int typeSize = 4;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUInts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        return MemoryMarshal.Cast<byte, uint>(span);
    }

    public static Span<ushort> DecodeUShorts(Span<byte> span)
    {
        const int typeSize = 2;
        if (span.Length % typeSize != 0)
        {
            throw new InvalidDataException(
                $"{nameof(DecodeUShorts)} expects input in multiples of {typeSize} and received {span.Length}");
        }

        return MemoryMarshal.Cast<byte, ushort>(span);
    }

    public static Span<bool> DecodeBools(Span<byte> span)
    {
        return MemoryMarshal.Cast<byte, bool>(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowTargetLength<T>(int targetLength, int expectedLength)
    {
        Type type = typeof(T);
        throw new InvalidDataException(
            $"Invalid target length in SSZ encoding of {type.Name}. Target length is {targetLength} and expected length is {expectedLength}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowSourceLength<T>(int sourceLength, int expectedLength)
    {
        Type type = typeof(T);
        throw new InvalidDataException(
            $"Invalid source length in SSZ decoding of {type.Name}. Source length is {sourceLength} and expected length is {expectedLength}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowInvalidSourceArrayLength<T>(int sourceLength, int expectedItemLength)
    {
        Type type = typeof(T);
        throw new InvalidDataException(
            $"Invalid source length in SSZ decoding of {type.Name}. Source length is {sourceLength} and expected length is a multiple of {expectedItemLength}.");
    }
}
