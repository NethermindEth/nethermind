// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Core;

/// <summary>
/// Represents the 128 data-column cells available for each blob in a transaction.
/// </summary>
/// <remarks>The wire representation is a fixed-width, little-endian 16-byte bit vector.</remarks>
public readonly record struct BlobCellMask(UInt128 Value)
{
    /// <summary>The byte length of the wire representation.</summary>
    public const int FixedByteLength = 16;

    /// <summary>The number of cells represented by the mask.</summary>
    public const int CellCount = 128;

    /// <summary>An empty cell mask.</summary>
    public static BlobCellMask Empty => default;

    /// <summary>A mask containing every cell.</summary>
    public static BlobCellMask Full => new(UInt128.MaxValue);

    /// <summary>Whether the mask contains no cells.</summary>
    public bool IsEmpty => Value == UInt128.Zero;

    /// <summary>Whether the mask contains every cell.</summary>
    public bool IsFull => Value == UInt128.MaxValue;

    /// <summary>The number of cells contained in the mask.</summary>
    public int Count
        => BitOperations.PopCount((ulong)(Value >> 64))
         + BitOperations.PopCount((ulong)Value);

    /// <summary>Returns whether the cell at <paramref name="index"/> is present.</summary>
    public bool Contains(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CellCount);
        return (Value & (UInt128.One << index)) != 0;
    }

    /// <summary>Returns the cells present in both masks.</summary>
    public BlobCellMask Intersect(BlobCellMask other) => new(Value & other.Value);

    /// <summary>Returns the cells present in either mask.</summary>
    public BlobCellMask Union(BlobCellMask other) => new(Value | other.Value);

    /// <summary>Returns the cells in this mask that are absent from <paramref name="other"/>.</summary>
    public BlobCellMask Except(BlobCellMask other) => new(Value & ~other.Value);

    /// <summary>Enumerates the indices of the cells present in the mask.</summary>
    public SetBitEnumerator EnumerateSetBits() => new(Value);

    /// <summary>Encodes the mask as a fixed-width, little-endian byte array.</summary>
    public byte[] ToBytes()
    {
        byte[] bytes = new byte[FixedByteLength];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Writes the fixed-width, little-endian representation to <paramref name="destination"/>.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < FixedByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), destination.Length, $"Blob cell mask destination must be at least {FixedByteLength} bytes.");
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)Value);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], (ulong)(Value >> 64));
    }

    /// <summary>Decodes a fixed-width, little-endian cell mask.</summary>
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

    /// <summary>Creates a mask containing the supplied cell indices.</summary>
    public static BlobCellMask FromIndices(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);

        UInt128 value = UInt128.Zero;
        foreach (int index in indices)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CellCount);
            value |= UInt128.One << index;
        }

        return new(value);
    }

    /// <summary>Returns the intersection of two masks.</summary>
    public static BlobCellMask operator &(BlobCellMask left, BlobCellMask right) => left.Intersect(right);

    /// <summary>Returns the union of two masks.</summary>
    public static BlobCellMask operator |(BlobCellMask left, BlobCellMask right) => left.Union(right);

    /// <summary>Enumerates set cell indices without allocating.</summary>
    public ref struct SetBitEnumerator(UInt128 value)
    {
        private UInt128 _value = value;

        /// <summary>The current cell index.</summary>
        public int Current { get; private set; }

        /// <summary>Returns this enumerator.</summary>
        public readonly SetBitEnumerator GetEnumerator() => this;

        /// <summary>Advances to the next present cell.</summary>
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
