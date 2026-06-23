// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Forward cursor over a <see cref="SortedTable"/> in ascending key order. Walks the data blocks in
/// order (block i at <c>i·BlockSize</c>), skipping each block's self-describing header and stopping at
/// its <c>recordsEnd</c> (never the zero-padding), reconstructing front-coded keys (the <c>cp = 0</c>
/// reset at every restart and block start makes the running key self-correct). A plain struct (not a
/// ref struct) so callers — the N-way merger and the scanner — can hold many in an array; it does not
/// store the reader, taking it via <see cref="MoveNext"/>. The current key is copied into an internal
/// buffer so it stays valid across reader-minting <see cref="MoveNext"/> calls in the merge.
/// </summary>
internal struct SortedTableEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private readonly long _tableOffset;
    private readonly long _numDataBlocks;
    private long _blockIdx;
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
            _numDataBlocks = footer.NumDataBlocks;
        _blockIdx = -1; // before the first block; the first MoveNext loads block 0 (_pos == _blockEnd == 0)
    }

    public bool MoveNext(scoped in TReader reader)
    {
        // Cross into the next data block(s), skipping each self-describing header.
        while (_pos >= _blockEnd)
        {
            _blockIdx++;
            if (_blockIdx >= _numDataBlocks) return false;
            long blockStart = _tableOffset + _blockIdx * SortedTable.BlockSize;
            if (!BlockReader.ReadHeader<TReader, TPin>(in reader, blockStart, out _, out long recordsEnd, out _, out long recordsStart))
                return false;
            _pos = blockStart + recordsStart;
            _blockEnd = blockStart + recordsEnd;
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
        _value = new Bound(valueSizeOffset + Block.SizePrefix, valueLength);

        _pos = valueSizeOffset + Block.SizePrefix + valueLength;
        return true;
    }

    public readonly ReadOnlySpan<byte> CurrentKey => _keyBuf.AsSpan(0, _keyLength);
    public readonly Bound CurrentValue => _value;
}
