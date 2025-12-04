// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Db.LogIndex;

// Arrays are expected to be convertible to long[] while remaining sorted
// Values positions are reversed (for little-endian systems), as BlockNumber should take priority in comparison
// TODO: handling for big-endian?
[StructLayout(LayoutKind.Explicit, Pack = sizeof(int))]
public readonly record struct LogPosition(
    [field: FieldOffset(4)] int BlockNumber,
    [field: FieldOffset(0)] int Index = 0
) : IComparable<LogPosition>
{
    public const int Size = sizeof(long);

    public static implicit operator long(LogPosition position) =>
        Unsafe.As<LogPosition, long>(ref Unsafe.AsRef(in position));

    public static implicit operator LogPosition(long position) =>
        Unsafe.As<long, LogPosition>(ref position);

    public int CompareTo(LogPosition other)
    {
        // TODO: compare performance
        // var val1Comparison = BlockNumber.CompareTo(other.BlockNumber);
        // return val1Comparison != 0 ? val1Comparison : Index.CompareTo(other.Index);

        return ((long)this).CompareTo(other);
    }

    public static bool TryParse(string input, out LogPosition position)
    {
        position = default;

        var parts = input.Split(':');
        if (parts.Length != 2) return false;

        if (!int.TryParse(parts[0], out var blockNumber)) return false;
        if (!int.TryParse(parts[1], out var index)) return false;

        position = new(blockNumber, index);
        return true;
    }

    public static LogPosition Parse(string input) => TryParse(input, out var position)
        ? position
        : throw new FormatException($"Invalid {nameof(LogPosition)} string: \"{input}\"");

    public byte[] ToArray()
    {
        var buffer = new byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, this);
        return buffer;
    }

    public override string ToString() => $"{BlockNumber}:{Index}";
}
