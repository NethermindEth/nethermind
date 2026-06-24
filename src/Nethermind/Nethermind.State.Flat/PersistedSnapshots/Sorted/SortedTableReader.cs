// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Lookup over a <see cref="SortedTable"/>: a ceiling search of the index block
/// (<see cref="IndexBlockReader.SeekCeiling"/>) selects a data block by byte offset, then a ceiling
/// search of that data block (<see cref="DataBlockReader.SeekCeiling"/>) resolves the exact key. Wire
/// layout: <see cref="SortedTable"/>.
/// </summary>
internal static class SortedTableReader
{
    /// <summary>
    /// Seek <paramref name="key"/> in the table occupying <paramref name="table"/>. On a hit returns
    /// the reader-absolute <see cref="Bound"/> of the matching record's value.
    /// </summary>
    internal static bool TrySeek<TReader, TPin>(scoped in TReader reader, Bound table, scoped ReadOnlySpan<byte> key, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        value = default;
        // An empty table has an empty index block (no restarts), so the stage-1 ceiling below returns
        // false — no separate empty check needed.
        if (!SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out SortedTable.Footer footer))
            return false;

        // Stage 1: ceiling over the index block — first separator ≥ target → its data block's table-relative
        // byte offset (index values are RocksDB-style delta-coded, reconstructed by IndexBlockReader).
        Span<byte> sepBuf = stackalloc byte[256];
        if (!IndexBlockReader.SeekCeiling<TReader, TPin>(in reader, SortedTable.IndexBlockStart(table, footer), key, sepBuf, out _, out long byteOffset))
            return false;

        // Stage 2: ceiling over the data block; a hit requires the ceiling key to equal the target.
        Span<byte> keyBuf = stackalloc byte[256];
        if (!DataBlockReader.SeekCeiling<TReader, TPin>(in reader, SortedTable.DataBlockStart(table, byteOffset), key, keyBuf, out int keyLen, out Bound v))
            return false;
        if (!key.SequenceEqual(keyBuf[..keyLen])) return false;

        value = v;
        return true;
    }
}
