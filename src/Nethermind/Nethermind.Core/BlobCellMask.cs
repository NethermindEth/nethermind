// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Core;

public readonly record struct BlobCellMask(UInt128 Value)
{
    public const int FixedByteLength = 16;
    public const int CellCount = 128;

    public static BlobCellMask Empty => default;
    public static BlobCellMask Full => new(UInt128.MaxValue);

    public bool IsEmpty => Value == UInt128.Zero;
    public bool IsFull => Value == UInt128.MaxValue;

    public int Count
        => BitOperations.PopCount((ulong)(Value >> 64))
         + BitOperations.PopCount((ulong)Value);

    public bool Contains(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CellCount);
        return (Value & (UInt128.One << index)) != 0;
    }

    public BlobCellMask Intersect(BlobCellMask other) => new(Value & other.Value);
    public BlobCellMask Union(BlobCellMask other) => new(Value | other.Value);

    public IEnumerable<int> EnumerateSetBits()
    {
        UInt128 value = Value;
        for (int i = 0; i < CellCount && value != UInt128.Zero; i++)
        {
            if ((value & UInt128.One) != 0)
            {
                yield return i;
            }

            value >>= 1;
        }
    }

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[FixedByteLength];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)(Value >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), (ulong)Value);
        return bytes;
    }

    public static BlobCellMask FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return Empty;
        }

        if (bytes.Length != FixedByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length, $"Blob cell mask must be exactly {FixedByteLength} bytes.");
        }

        return new(
            ((UInt128)BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]) << 64)
            | BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    public static BlobCellMask FromIndices(IEnumerable<int> indices)
    {
        UInt128 value = UInt128.Zero;
        foreach (int index in indices)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CellCount);
            value |= UInt128.One << index;
        }

        return new(value);
    }

    public static BlobCellMask operator &(BlobCellMask left, BlobCellMask right) => left.Intersect(right);
    public static BlobCellMask operator |(BlobCellMask left, BlobCellMask right) => left.Union(right);
}
