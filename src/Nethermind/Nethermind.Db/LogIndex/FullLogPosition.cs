// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Db.LogIndex;

// TODO: handling for big-endian in implementations?

// Arrays are expected to be convertible to long[] while remaining sorted
// Values positions are reversed (for little-endian systems), as BlockNumber should take priority in comparison
[StructLayout(LayoutKind.Explicit, Pack = sizeof(int))]
public readonly record struct FullLogPosition(
    [field: FieldOffset(4)] int BlockNumber,
    [field: FieldOffset(0)] int Index = 0
) : ILogPosition<FullLogPosition>
{
    public static int Size => sizeof(long);

    public static bool operator <(FullLogPosition p1, FullLogPosition p2) => p1.CompareTo(p2) < 0;
    public static bool operator >(FullLogPosition p1, FullLogPosition p2) => p2.CompareTo(p1) < 0;

    public void WriteFirstTo(Span<byte> dbValue) => BinaryPrimitives.WriteInt64LittleEndian(dbValue, this + 1);
    public void WriteLastTo(Span<byte> dbValue) => BinaryPrimitives.WriteInt64LittleEndian(dbValue[^Size..], this + 1);
    public static FullLogPosition ReadFirstFrom(ReadOnlySpan<byte> dbValue) => BinaryPrimitives.ReadInt64LittleEndian(dbValue) - 1;
    public static FullLogPosition ReadLastFrom(ReadOnlySpan<byte> dbValue) => BinaryPrimitives.ReadInt64LittleEndian(dbValue[^Size..]) - 1;

    public static implicit operator long(FullLogPosition position) =>
        Unsafe.As<FullLogPosition, long>(ref Unsafe.AsRef(in position));

    public static implicit operator FullLogPosition(long position) =>
        Unsafe.As<long, FullLogPosition>(ref position);

    public static FullLogPosition Create(int blockNumber) => new(blockNumber);
    public static FullLogPosition Create(int blockNumber, int logIndex) => new(blockNumber, logIndex);

    public int CompareTo(FullLogPosition other)
    {
        // TODO: compare performance
        // var val1Comparison = BlockNumber.CompareTo(other.BlockNumber);
        // return val1Comparison != 0 ? val1Comparison : Index.CompareTo(other.Index);

        return ((long)this).CompareTo(other);
    }

    public static bool TryParse(string input, out FullLogPosition position)
    {
        position = default;

        var parts = input.Split(':');

        if (parts.Length == 1)
        {
            if (!long.TryParse(input, out var value)) return false;

            position = value;
            return true;
        }

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out var blockNumber)) return false;
            if (!int.TryParse(parts[1], out var index)) return false;

            position = new(blockNumber, index);
            return true;
        }

        return false;
    }

    public static FullLogPosition Parse(string input) => TryParse(input, out FullLogPosition position)
        ? position
        : throw new FormatException($"Invalid {nameof(FullLogPosition)} string: \"{input}\"");

    public byte[] ToArray()
    {
        var buffer = new byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, this);
        return buffer;
    }

    public override string ToString() => $"{BlockNumber}:{Index}";
}
