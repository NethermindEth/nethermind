// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Forward cursor over a <see cref="SortedTable"/> in ascending key order. Records are stored sorted
/// and contiguous, so this is a straight sequential walk of the records region — no offset
/// indirection. A plain struct (not a ref struct) so callers — the N-way merger and the scanner —
/// can hold many in an array; it does not store the reader, taking it via <see cref="MoveNext"/>.
/// The current key is copied into an internal buffer so it stays valid across reader-minting
/// <see cref="MoveNext"/> calls in the merge.
/// </summary>
internal struct SortedTableEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private long _pos;
    private long _recordsEnd;
    private byte[] _keyBuf;
    private int _keyLength;
    private Bound _value;

    public SortedTableEnumerator(scoped in TReader reader, Bound table)
    {
        // Fixed: keys are ≤ 255 bytes, and the running key must retain its prefix across records.
        _keyBuf = new byte[256];
        if (SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out _, out _, out long offsetRegionStart))
        {
            _pos = table.Offset;
            _recordsEnd = offsetRegionStart;
        }
    }

    public bool MoveNext(scoped in TReader reader)
    {
        if (_pos >= _recordsEnd) return false;

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
