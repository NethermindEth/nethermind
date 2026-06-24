// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Forward cursor over a <see cref="SortedTable"/> in ascending key order. Visits the data blocks by
/// reading the index block in order — each index record's value is the next data block's table-relative
/// byte offset (RocksDB-style delta-coded) — so it makes no assumption about data-block alignment. Within
/// a block it skips the self-describing header and stops at <c>recordsEnd</c> (never the zero-padding),
/// reconstructing front-coded keys (the <c>cp = 0</c> reset at every restart and block start makes the
/// running key self-correct). A plain struct (not a ref struct) so callers — the N-way merger and the
/// scanner — can hold many in an array; it does not store the reader, taking it via
/// <see cref="MoveNext"/>. The current key is copied into an internal off-heap buffer so it stays valid
/// across reader-minting <see cref="MoveNext"/> calls in the merge; callers must <see cref="Dispose"/>
/// the enumerator (or iterate it via <c>foreach</c>) to release it.
/// </summary>
internal struct SortedTableEnumerator<TReader, TPin> : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IByteReader<TPin>, allows ref struct
{
    private readonly long _tableOffset;
    // Index-block cursor: walks (separator → data-block byte offset) records in order, decoding the
    // delta-coded offsets, to locate each data block. _indexPos == _indexEnd ⇒ no more data blocks.
    private long _indexPos;
    private long _indexEnd;
    private long _indexRunningValue;
    // Data-block cursor within the block located by the index.
    private long _pos;
    private long _blockEnd;
    private readonly NativeMemoryList<byte> _keyBuf;
    private int _keyLength;
    private Bound _value;

    public SortedTableEnumerator(scoped in TReader reader, Bound table)
    {
        // Fixed-size running key: keys are ≤ 255 bytes, and records are written in place at their
        // common-prefix offset, so the buffer is pre-sized to its full length up front.
        _keyBuf = new NativeMemoryList<byte>(256, 256);
        _tableOffset = table.Offset;
        if (SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out SortedTable.Footer footer))
        {
            long indexStart = SortedTable.IndexBlockStart(table, footer);
            if (IndexBlockReader.TryReadRecordRange<TReader, TPin>(in reader, indexStart, out long recordsStart, out long recordsEnd))
            {
                _indexPos = indexStart + recordsStart;
                _indexEnd = indexStart + recordsEnd;
            }
        }
        // _pos == _blockEnd == 0 ⇒ the first MoveNext pulls the first data block from the index.
    }

    public bool MoveNext(scoped in TReader reader)
    {
        // Cross into the next data block(s) by reading the index, skipping each block's self-describing
        // header. The loop also tolerates a (never produced) empty data block.
        while (_pos >= _blockEnd)
            if (!TryAdvanceToNextDataBlock(in reader)) return false;

        Span<byte> hdr = stackalloc byte[2]; // [commonPrefix u8][suffixLen u8]
        if (!reader.TryRead(_pos, hdr)) return false;
        int cp = hdr[0];
        int suffixLen = hdr[1];
        // Front-coded: keep _keyBuf[0..cp) from the previous record, append this record's suffix.
        if (!reader.TryRead(_pos + 2, _keyBuf.AsSpan().Slice(cp, suffixLen))) return false;
        _keyLength = cp + suffixLen;

        long valueSizeOffset = _pos + 2 + suffixLen;
        if (!reader.TryRead(valueSizeOffset, hdr[..1])) return false;
        int valueLength = hdr[0];
        _value = new Bound(valueSizeOffset + Block.SizePrefix, valueLength);

        _pos = valueSizeOffset + Block.SizePrefix + valueLength;
        return true;
    }

    /// <summary>Read the next index record's data-block byte offset (reconstructing the delta-coded value)
    /// and position the data cursor at that block's first record. Returns <c>false</c> when the index is
    /// exhausted or a record/header cannot be read.</summary>
    private bool TryAdvanceToNextDataBlock(scoped in TReader reader)
    {
        if (_indexPos >= _indexEnd) return false;

        // Index record: [cp u8][suffixLen u8][keySuffix][vs u8][value]. Only the value (the data block's
        // table-relative byte offset) is needed — absolute at a restart head, a delta against the previous
        // record in between (see BlockBuilder.AddDeltaValue); the separator key is skipped over.
        Span<byte> hdr = stackalloc byte[2];
        if (!reader.TryRead(_indexPos, hdr)) return false;
        int cp = hdr[0];
        int suffixLen = hdr[1];
        long valueSizeOffset = _indexPos + 2 + suffixLen;
        if (!reader.TryRead(valueSizeOffset, hdr[..1])) return false;
        int valueLen = hdr[0];
        if (valueLen > 6) return false; // u48 ceiling — reject corruption before the shift widens it

        Span<byte> vbuf = stackalloc byte[sizeof(ulong)];
        vbuf.Clear();
        if (valueLen > 0 && !reader.TryRead(valueSizeOffset + Block.SizePrefix, vbuf[..valueLen])) return false;
        long stored = (long)BinaryPrimitives.ReadUInt64LittleEndian(vbuf);
        // A restart record (cp == 0) re-anchors to an absolute offset; in between, a delta. The index walk
        // starts at the first record (cp == 0), so the running offset is anchored before any delta.
        _indexRunningValue = cp == 0 ? stored : _indexRunningValue + stored;
        _indexPos = valueSizeOffset + Block.SizePrefix + valueLen;

        long blockStart = _tableOffset + _indexRunningValue;
        if (!BlockReader.TryReadRecordRange<TReader, TPin>(in reader, blockStart, out long recordsStart, out long recordsEnd))
            return false;
        _pos = blockStart + recordsStart;
        _blockEnd = blockStart + recordsEnd;
        return true;
    }

    public readonly ReadOnlySpan<byte> CurrentKey => _keyBuf.AsSpan()[.._keyLength];
    public readonly Bound CurrentValue => _value;

    public void Dispose() => _keyBuf.Dispose();
}
