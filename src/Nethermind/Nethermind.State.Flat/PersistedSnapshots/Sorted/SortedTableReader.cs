// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Lookup over a <see cref="SortedTable"/>: a ceiling search of the index block selects a data block
/// number, then a ceiling search of that data block resolves the exact key. Two
/// <see cref="BlockReader.SeekCeiling"/> calls. Wire layout: <see cref="SortedTable"/>.
/// </summary>
internal static class SortedTableReader
{
    /// <summary>
    /// Seek <paramref name="key"/> in the table occupying <paramref name="table"/>. On a hit returns
    /// the reader-absolute <see cref="Bound"/> of the matching record's value.
    /// </summary>
    internal static bool TrySeek<TReader, TPin>(scoped in TReader reader, Bound table, scoped ReadOnlySpan<byte> key, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        value = default;
        if (!SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out SortedTable.Footer footer)
            || footer.NumDataBlocks == 0)
            return false;

        // Stage 1: ceiling over the index block — first separator ≥ target → its data block number.
        Span<byte> sepBuf = stackalloc byte[256];
        if (!BlockReader.SeekCeiling<TReader, TPin>(in reader, SortedTable.IndexBlockStart(table, footer), key, sepBuf, out _, out Bound blockRef))
            return false;

        Span<byte> bn = stackalloc byte[SortedTable.IndexValueSize];
        if (!reader.TryRead(blockRef.Offset, bn)) return false;
        long blockNumber = BinaryPrimitives.ReadUInt32LittleEndian(bn);

        // Stage 2: ceiling over the data block; a hit requires the ceiling key to equal the target.
        Span<byte> keyBuf = stackalloc byte[256];
        if (!BlockReader.SeekCeiling<TReader, TPin>(in reader, SortedTable.DataBlockStart(table, blockNumber), key, keyBuf, out int keyLen, out Bound v))
            return false;
        if (!key.SequenceEqual(keyBuf[..keyLen])) return false;

        value = v;
        return true;
    }
}
