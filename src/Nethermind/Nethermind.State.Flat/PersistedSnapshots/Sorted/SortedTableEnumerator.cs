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
/// byte offset (changed-prefix coded) — so it makes no assumption about data-block alignment. Within
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
    // Reader-absolute start of the index block, retained so Seek can re-run its ceiling search.
    private long _indexStart;
    // Index-block cursor: walks (separator → data-block byte offset) records in order, decoding the
    // changed-prefix-coded offsets, to locate each data block. _indexPos == _indexEnd ⇒ no more data blocks.
    private long _indexPos;
    private long _indexEnd;
    // Running index value (a data-block byte offset); each record overwrites only its changed low bytes.
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
                _indexStart = indexStart;
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

    /// <summary>
    /// Reposition the cursor so a subsequent forward scan reaches records at or after <paramref name="target"/>.
    /// Lands the data cursor at the start of the block whose ceiling separator ≥ <paramref name="target"/> and
    /// resumes the index walk just past that block, so the caller skips within that one block to the exact
    /// ceiling. Returns <c>false</c> — leaving the cursor exhausted — when the table is empty or every key is
    /// &lt; <paramref name="target"/>.
    /// </summary>
    public bool Seek(scoped in TReader reader, scoped ReadOnlySpan<byte> target)
    {
        _pos = _blockEnd = 0; // force MoveNext to pull the sought block; a failure below leaves it exhausted
        if (_indexEnd == 0) return false; // no valid index (empty/footer-less table)

        Span<byte> sepBuf = stackalloc byte[256];
        if (!IndexBlockReader.SeekCeiling<TReader, TPin>(in reader, _indexStart, target, sepBuf, out _, out long byteOffset, out long ceilingRecordEnd))
        {
            _indexPos = _indexEnd; // every separator < target ⇒ nothing at or after it
            return false;
        }

        // Resume the index walk just past the ceiling record; its reconstructed value is this block's offset,
        // carried over as the running high bytes for the next changed-prefix-coded index record.
        _indexPos = ceilingRecordEnd;
        _indexRunningValue = byteOffset;

        long blockStart = _tableOffset + byteOffset;
        if (!DataBlockReader.TryReadRecordRange<TReader, TPin>(in reader, blockStart, out long recordsStart, out long recordsEnd))
            return false;
        _pos = blockStart + recordsStart;
        _blockEnd = blockStart + recordsEnd;
        return true;
    }

    /// <summary>Read the next index record's data-block byte offset (reconstructing the changed-prefix value)
    /// and position the data cursor at that block's first record. Returns <c>false</c> when the index is
    /// exhausted or a record/header cannot be read.</summary>
    private bool TryAdvanceToNextDataBlock(scoped in TReader reader)
    {
        if (_indexPos >= _indexEnd) return false;

        // Index record: [cp u8][suffixLen u8][valChangedLen u8][keySuffix][valChanged]. Only the value (the
        // data block's table-relative byte offset) is needed — its changed low bytes overwrite the running
        // value in place (see BlockBuilder.AddChangedPrefixValue); the separator key is skipped over.
        Block.IndexRecordHeader rec = default;
        if (!reader.TryRead(_indexPos, MemoryMarshal.AsBytes(new Span<Block.IndexRecordHeader>(ref rec)))) return false;
        int valChangedLen = rec.ValueChangedLength;
        if (valChangedLen > 6) return false; // > u48 ⇒ corrupt

        long valueStart = _indexPos + Unsafe.SizeOf<Block.IndexRecordHeader>() + rec.SuffixLength;
        // A restart (cp == 0) drops the previous record's high bytes; the walk's first record is a restart,
        // so the running value is reset before any record that keeps high bytes.
        if (rec.CommonPrefix == 0) _indexRunningValue = 0;
        if (valChangedLen > 0 &&
            !reader.TryRead(valueStart, MemoryMarshal.AsBytes(new Span<long>(ref _indexRunningValue))[..valChangedLen])) return false;
        _indexPos = valueStart + valChangedLen;

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
