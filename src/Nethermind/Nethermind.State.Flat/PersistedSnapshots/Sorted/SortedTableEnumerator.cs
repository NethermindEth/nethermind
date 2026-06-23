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
        _keyBuf = new byte[64];
        if (SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out _, out _, out long offsetRegionStart))
        {
            _pos = table.Offset;
            _recordsEnd = offsetRegionStart;
        }
    }

    public bool MoveNext(scoped in TReader reader)
    {
        if (_pos >= _recordsEnd) return false;

        Span<byte> sizeBuf = stackalloc byte[SortedTable.SizePrefix];
        if (!reader.TryRead(_pos, sizeBuf)) return false;
        int keyLength = sizeBuf[0];
        if (keyLength > _keyBuf.Length) _keyBuf = new byte[keyLength];
        long keyOffset = _pos + SortedTable.SizePrefix;
        if (!reader.TryRead(keyOffset, _keyBuf.AsSpan(0, keyLength))) return false;
        _keyLength = keyLength;

        long valueSizeOffset = keyOffset + keyLength;
        if (!reader.TryRead(valueSizeOffset, sizeBuf)) return false;
        int valueLength = sizeBuf[0];
        _value = new Bound(valueSizeOffset + SortedTable.SizePrefix, valueLength);

        _pos = valueSizeOffset + SortedTable.SizePrefix + valueLength;
        return true;
    }

    public readonly ReadOnlySpan<byte> CurrentKey => _keyBuf.AsSpan(0, _keyLength);
    public readonly Bound CurrentValue => _value;
}
