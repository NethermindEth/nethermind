// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Forward cursor over a <see cref="SortedTable"/> in ascending key order, walking the offset
/// region entry by entry. A plain struct (not a ref struct) so callers — the N-way merger and the
/// scanner — can hold many in an array. It does not store the reader, taking it via
/// <see cref="MoveNext"/>. The current key is copied into an internal buffer so it stays valid
/// across reader-minting <see cref="MoveNext"/> calls in the merge.
/// </summary>
internal struct SortedTableEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private readonly Bound _table;
    private readonly long _count;
    private readonly long _offsetRegionStart;
    private long _index;
    private byte[] _keyBuf;
    private int _keyLength;
    private Bound _value;

    public SortedTableEnumerator(scoped in TReader reader, Bound table)
    {
        _keyBuf = new byte[64];
        if (SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out long count, out long offsetRegionStart))
        {
            _table = table;
            _count = count;
            _offsetRegionStart = offsetRegionStart;
        }
    }

    public bool MoveNext(scoped in TReader reader)
    {
        if (_index >= _count) return false;

        Span<byte> tmp = stackalloc byte[SortedTable.OffsetSize];
        if (!reader.TryRead(_offsetRegionStart + _index * SortedTable.OffsetSize, tmp)) return false;
        long recordStart = _table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(tmp);

        Span<byte> sizeBuf = stackalloc byte[SortedTable.SizePrefix];
        if (!reader.TryRead(recordStart, sizeBuf)) return false;
        int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(sizeBuf);
        if (keyLength > _keyBuf.Length) _keyBuf = new byte[keyLength];
        if (!reader.TryRead(recordStart + SortedTable.SizePrefix, _keyBuf.AsSpan(0, keyLength))) return false;
        _keyLength = keyLength;

        long valueSizeOffset = recordStart + SortedTable.SizePrefix + keyLength;
        if (!reader.TryRead(valueSizeOffset, sizeBuf)) return false;
        int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(sizeBuf);
        _value = new Bound(valueSizeOffset + SortedTable.SizePrefix, valueLength);

        _index++;
        return true;
    }

    public readonly ReadOnlySpan<byte> CurrentKey => _keyBuf.AsSpan(0, _keyLength);
    public readonly Bound CurrentValue => _value;
}
