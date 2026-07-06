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

    public SetBitEnumerator EnumerateSetBits() => new(Value);

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[FixedByteLength];
        WriteTo(bytes);
        return bytes;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < FixedByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), destination.Length, $"Blob cell mask destination must be at least {FixedByteLength} bytes.");
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)Value);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], (ulong)(Value >> 64));
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
            ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) << 64)
            | BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]));
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

    public ref struct SetBitEnumerator(UInt128 value)
    {
        private UInt128 _value = value;

        public int Current { get; private set; }

        public readonly SetBitEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_value == UInt128.Zero)
            {
                return false;
            }

            ulong low = (ulong)_value;
            Current = low != 0
                ? BitOperations.TrailingZeroCount(low)
                : 64 + BitOperations.TrailingZeroCount((ulong)(_value >> 64));

            // Clear the lowest set bit.
            _value &= _value - UInt128.One;
            return true;
        }
    }
}
