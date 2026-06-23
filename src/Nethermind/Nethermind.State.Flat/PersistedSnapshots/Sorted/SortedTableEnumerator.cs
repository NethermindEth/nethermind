// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Forward cursor over a <see cref="SortedTable"/> in ascending key order. Walks the data blocks in
/// order, skipping each block's restart-table header and reconstructing front-coded keys (the
/// <c>cp = 0</c> reset at every restart and block start makes the running key self-correct). A plain
/// struct (not a ref struct) so callers — the N-way merger and the scanner — can hold many in an
/// array; it does not store the reader, taking it via <see cref="MoveNext"/>. The current key is
/// copied into an internal buffer so it stays valid across reader-minting <see cref="MoveNext"/> calls
/// in the merge.
/// </summary>
internal struct SortedTableEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private readonly long _tableOffset;
    private readonly long _blockOffsetsStart;
    private readonly int _numBlocks;
    private int _blockIdx;
    private long _pos;
    private long _blockEnd;
    private byte[] _keyBuf;
    private int _keyLength;
    private Bound _value;

    public SortedTableEnumerator(scoped in TReader reader, Bound table)
    {
        // Fixed: keys are ≤ 255 bytes, and the running key must retain its prefix across records.
        _keyBuf = new byte[256];
        _tableOffset = table.Offset;
        if (SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out SortedTable.Footer footer))
        {
            _numBlocks = footer.NumBlocks;
            _blockOffsetsStart = footer.BlockOffsetsStart;
        }
        _blockIdx = -1; // before the first block; the first MoveNext loads block 0 (_pos == _blockEnd == 0)
    }

    public bool MoveNext(scoped in TReader reader)
    {
        Span<byte> ob = stackalloc byte[SortedTable.IndexOffsetSize];
        // Cross into the next data block(s), skipping each restart-table header.
        while (_pos >= _blockEnd)
        {
            _blockIdx++;
            if (_blockIdx >= _numBlocks) return false;

            if (!reader.TryRead(_blockOffsetsStart + (long)_blockIdx * SortedTable.IndexOffsetSize, ob)) return false;
            long blockStart = _tableOffset + BinaryPrimitives.ReadUInt32LittleEndian(ob);
            if (!reader.TryRead(_blockOffsetsStart + (long)(_blockIdx + 1) * SortedTable.IndexOffsetSize, ob)) return false;
            _blockEnd = _tableOffset + BinaryPrimitives.ReadUInt32LittleEndian(ob);

            if (!reader.TryRead(blockStart, ob[..SortedTable.RestartOffsetSize])) return false;
            int numRestarts = BinaryPrimitives.ReadUInt16LittleEndian(ob);
            _pos = blockStart + (long)(numRestarts + 1) * SortedTable.RestartOffsetSize; // past [numRestarts][restart table]
        }

        Span<byte> hdr = stackalloc byte[2]; // [commonPrefix u8][suffixLen u8]
        if (!reader.TryRead(_pos, hdr)) return false;
        int cp = hdr[0];
        int suffixLen = hdr[1];
        // Front-coded: keep _keyBuf[0..cp) from the previous record, append this record's suffix.
        if (!reader.TryRead(_pos + 2, _keyBuf.AsSpan(cp, suffixLen))) return false;
        _keyLength = cp + suffixLen;

        long valueSizeOffset = _pos + 2 + suffixLen;
        if (!reader.TryRead(valueSizeOffset, hdr[..1])) return false;
        int valueLength = hdr[0];
        _value = new Bound(valueSizeOffset + SortedTable.SizePrefix, valueLength);

        _pos = valueSizeOffset + SortedTable.SizePrefix + valueLength;
        return true;
    }

    public readonly ReadOnlySpan<byte> CurrentKey => _keyBuf.AsSpan(0, _keyLength);
    public readonly Bound CurrentValue => _value;
}
