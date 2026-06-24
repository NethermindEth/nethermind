// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Forward cursor over a <see cref="SortedTable"/> in ascending key order. Visits the data blocks by
/// reading the index block in order — each index record's value is the next data block's table-relative
/// byte offset (front-coded) — so it makes no assumption about data-block alignment. Within
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
    // front-coded offsets, to locate each data block. _indexPos == _indexEnd ⇒ no more data blocks.
    private long _indexPos;
    private long _indexEnd;
    // Running front-coded index value (a data-block byte offset) and its min-width big-endian byte count.
    private long _indexRunningValue;
    private int _indexValueWidth;
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

        Block.DataRecordHeader rec = default;
        if (!reader.TryRead(_pos, MemoryMarshal.AsBytes(new Span<Block.DataRecordHeader>(ref rec)))) return false;
        int cp = rec.CommonPrefix;
        int suffixLen = rec.SuffixLength;
        long keyStart = _pos + Unsafe.SizeOf<Block.DataRecordHeader>();
        // Front-coded: keep _keyBuf[0..cp) from the previous record, append this record's suffix.
        if (!reader.TryRead(keyStart, _keyBuf.AsSpan().Slice(cp, suffixLen))) return false;
        _keyLength = cp + suffixLen;

        // The 3-byte prefix blit carried the value length, so the value is just a Bound past the key.
        long valueStart = keyStart + suffixLen;
        _value = new Bound(valueStart, rec.ValueLength);
        _pos = valueStart + rec.ValueLength;
        return true;
    }

    /// <summary>Read the next index record's data-block byte offset (reconstructing the front-coded value)
    /// and position the data cursor at that block's first record. Returns <c>false</c> when the index is
    /// exhausted or a record/header cannot be read.</summary>
    private bool TryAdvanceToNextDataBlock(scoped in TReader reader)
    {
        if (_indexPos >= _indexEnd) return false;

        // Index record: [cp u8][suffixLen u8][valCp u8][valSuffixLen u8][keySuffix][valSuffix]. Only the
        // value (the data block's table-relative byte offset) is needed — front-coded against the previous
        // record's value (see BlockBuilder.AddFrontCodedValue); the separator key is skipped over.
        Block.IndexRecordHeader rec = default;
        if (!reader.TryRead(_indexPos, MemoryMarshal.AsBytes(new Span<Block.IndexRecordHeader>(ref rec)))) return false;
        int valCp = rec.ValueCommonPrefix;
        int valSuffixLen = rec.ValueSuffixLength;
        if (valCp > _indexValueWidth || valCp + valSuffixLen > 6) return false; // corrupt front-coding / > u48

        long valueStart = _indexPos + Unsafe.SizeOf<Block.IndexRecordHeader>() + rec.SuffixLength;
        Span<byte> valSuffix = stackalloc byte[6]; // u48
        if (valSuffixLen > 0 && !reader.TryRead(valueStart, valSuffix[..valSuffixLen])) return false;
        // The index walk starts at the first record (cp == 0 ⇒ valCp == 0), so the running offset is fully
        // restated before any front-coded record.
        _indexRunningValue = Block.FrontDecodeValue(_indexRunningValue, _indexValueWidth, valCp, valSuffix[..valSuffixLen]);
        _indexValueWidth = valCp + valSuffixLen;
        _indexPos = valueStart + valSuffixLen;

        long blockStart = _tableOffset + _indexRunningValue;
        if (!DataBlockReader.TryReadRecordRange<TReader, TPin>(in reader, blockStart, out long recordsStart, out long recordsEnd))
            return false;
        _pos = blockStart + recordsStart;
        _blockEnd = blockStart + recordsEnd;
        return true;
    }

    public readonly ReadOnlySpan<byte> CurrentKey => _keyBuf.AsSpan()[.._keyLength];
    public readonly Bound CurrentValue => _value;

    public void Dispose() => _keyBuf.Dispose();
}
