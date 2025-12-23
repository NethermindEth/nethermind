// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;

namespace Nethermind.Db.LogIndex;

public readonly record struct BlockNumLogPosition(int BlockNumber): ILogPosition<BlockNumLogPosition>
{
    public static int Size => sizeof(int);

    public static bool operator <(BlockNumLogPosition p1, BlockNumLogPosition p2) => p1.BlockNumber < p2.BlockNumber;
    public static bool operator >(BlockNumLogPosition p1, BlockNumLogPosition p2) => p1.BlockNumber > p2.BlockNumber;

    public void WriteFirstTo(Span<byte> dbValue) => BinaryPrimitives.WriteInt32LittleEndian(dbValue, BlockNumber + 1);
    public void WriteLastTo(Span<byte> dbValue) => BinaryPrimitives.WriteInt32LittleEndian(dbValue[^Size..], BlockNumber + 1);
    public static BlockNumLogPosition ReadFirstFrom(ReadOnlySpan<byte> dbValue) => BinaryPrimitives.ReadInt32LittleEndian(dbValue) - 1;
    public static BlockNumLogPosition ReadLastFrom(ReadOnlySpan<byte> dbValue) => BinaryPrimitives.ReadInt32LittleEndian(dbValue[^Size..]) - 1;

    public static implicit operator int(BlockNumLogPosition position) => position.BlockNumber;
    public static implicit operator BlockNumLogPosition(int position) => new(position);
    public static BlockNumLogPosition Create(int blockNumber) => new(blockNumber);

    public int CompareTo(BlockNumLogPosition other) => BlockNumber.CompareTo(other.BlockNumber);

    public static bool TryParse(string input, out BlockNumLogPosition position)
    {
        position = default;

        if (!int.TryParse(input, out var blockNum))
            return false;

        position = new(blockNum);
        return true;
    }

    public static BlockNumLogPosition Parse(string input) => TryParse(input, out BlockNumLogPosition position)
        ? position
        : throw new FormatException($"Invalid {nameof(BlockNumLogPosition)} string: \"{input}\"");

    public byte[] ToArray()
    {
        var buffer = new byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, this);
        return buffer;
    }

    public override string ToString() => BlockNumber.ToString();
}
