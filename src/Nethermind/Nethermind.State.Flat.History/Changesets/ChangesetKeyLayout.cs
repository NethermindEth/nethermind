// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Block-major keys: every transaction of one block is one contiguous range, so the writes made before a
/// given transaction are a single scan, and everything below a floor is a single range delete.</summary>
internal static class ChangesetKeyLayout
{
    public const int MaxTransactionIndex = ushort.MaxValue;
    public const int RowKeyLength = 1 + sizeof(ulong) + sizeof(ushort);
    public const int BlockKeyLength = 1 + sizeof(ulong);

    private const byte RowMarker = 0x00;
    private const byte BlockMarker = 0x01;
    private const byte MetadataMarker = 0xFF;
    private const byte CoverageDiscriminator = 0x01;

    public static int WriteRowKey(Span<byte> destination, ulong block, ushort transactionIndex)
    {
        destination[0] = RowMarker;
        BinaryPrimitives.WriteUInt64BigEndian(destination[1..], block);
        BinaryPrimitives.WriteUInt16BigEndian(destination[(1 + sizeof(ulong))..], transactionIndex);
        return RowKeyLength;
    }

    public static ushort TransactionIndexOf(scoped ReadOnlySpan<byte> rowKey) =>
        BinaryPrimitives.ReadUInt16BigEndian(rowKey[(1 + sizeof(ulong))..]);

    public static ulong BlockOf(scoped ReadOnlySpan<byte> rowKey) => BinaryPrimitives.ReadUInt64BigEndian(rowKey[1..]);

    public static bool IsRowKey(scoped ReadOnlySpan<byte> key) => key.Length == RowKeyLength && key[0] == RowMarker;

    public static int WriteBlockKey(Span<byte> destination, ulong block)
    {
        destination[0] = BlockMarker;
        BinaryPrimitives.WriteUInt64BigEndian(destination[1..], block);
        return BlockKeyLength;
    }

    public static int WriteCoverageKey(Span<byte> destination)
    {
        destination[0] = MetadataMarker;
        destination[1] = CoverageDiscriminator;
        return 2;
    }
}
