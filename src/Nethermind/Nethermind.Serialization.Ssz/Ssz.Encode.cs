// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// https://github.com/ethereum/consensus-specs/blob/dev/ssz/simple-serialize.md
/// </summary>
public static partial class Ssz
{
    private const int VarOffsetSize = sizeof(uint);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, byte[] value, ref int offset)
    {
        Encode(span.Slice(offset, value.Length), value);
        offset += value.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, int value, ref int offset)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), value);
        offset += sizeof(int);
    }

    public static void Encode(Span<byte> span, uint value, ref int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, sizeof(uint)), value);
        offset += sizeof(uint);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, ulong value, ref int offset)
    {
        Encode(span.Slice(offset, sizeof(ulong)), value);
        offset += sizeof(ulong);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, bool value, ref int offset)
    {
        Encode(span.Slice(offset, 1), value);
        offset++;
    }

    public static void Encode(Span<byte> span, byte value) => span[0] = value;

    public static void Encode(Span<byte> span, byte value, ref int offset) => span[offset++] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(span, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, int value) => BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(span, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(span, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, long value) => BinaryPrimitives.WriteInt64LittleEndian(span, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span[..8], (ulong)(value & ulong.MaxValue));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8, 8), (ulong)(value >> 64));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(Span<byte> span, bool value) => span[0] = Encode(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Encode(bool value) => value ? (byte)1 : (byte)0;

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

    public static void Encode(Span<byte> span, Span<byte> value, ref int offset, ref int dynamicOffset)
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

    public static void Encode(Span<byte> span, BitArray? vector)
    {
        if (vector is null)
        {
            return;
        }

        EncodeVector(span, vector);
    }

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

    public static void Encode(Span<byte> span, BitArray? list, int limit)
    {
        if (list is null)
        {
            return;
        }

        EncodeList(span, list);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowTargetLength<T>(int targetLength, int expectedLength)
    {
        Type type = typeof(T);
        throw new InvalidDataException(
            $"Invalid target length in SSZ encoding of {type.Name}. Target length is {targetLength} and expected length is {expectedLength}.");
    }
}
