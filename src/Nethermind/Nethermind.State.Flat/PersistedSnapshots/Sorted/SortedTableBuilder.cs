// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Builds a <see cref="SortedTable"/> by streaming: records must be <see cref="Add"/>ed in strictly
/// ascending key order and are written straight into 4 KiB-aligned data blocks as they arrive — no
/// record buffer, so the table size is bounded by the data region (16 TiB) rather than by an in-memory
/// buffer. The index (separator → block number) accrues one entry per flushed data block; at
/// <see cref="Build"/> the final data block and the single index block are emitted, followed by the footer.
/// </summary>
/// <remarks>
/// Both the data blocks and the index reuse <see cref="BlockBuilder"/>. Each finished data block but the
/// last is zero-padded to <see cref="SortedTable.BlockSize"/> so block <c>i</c> sits at <c>i·BlockSize</c>
/// and is addressed by block number; the index block is written right after the last (unpadded) data
/// block and located by the footer's <c>indexOffset</c>. The index entry for a block is the shortest
/// separator between that block's last key and the next block's first key (the last block uses its own
/// last key). Only the current data block and the index are buffered.
/// </remarks>
internal ref struct SortedTableBuilder<TWriter> where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private readonly long _tableStart;
    private readonly int _restartInterval;
    private readonly BlockBuilder _dataBlock;
    private readonly BlockBuilder _indexBlock;
    // Last key Added overall — also the last key of the current data block, used to enforce ascending
    // order and to derive the separator when a block flushes. Keys are ≤ 255 bytes.
    private readonly byte[] _prevKey;
    private int _prevKeyLen;
    // Number of data blocks flushed so far == the block number to assign to the next flushed block.
    private long _blockNumber;
    private long _count;

    public SortedTableBuilder(ref TWriter writer, int restartInterval = SortedTable.DefaultRestartInterval)
    {
        _writer = ref writer;
        _tableStart = writer.Written;
        _restartInterval = restartInterval;
        _dataBlock = new BlockBuilder(restartInterval, SortedTable.BlockSize);
        _indexBlock = new BlockBuilder(restartInterval);
        _prevKey = new byte[256];
    }

    /// <summary>Stream one record. Keys must arrive in strictly ascending order and be unique; key and
    /// value lengths must each be ≤ 255.</summary>
    /// <exception cref="ArgumentException">The key is not strictly greater than the previous key.</exception>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (_count > 0 && ((ReadOnlySpan<byte>)_prevKey.AsSpan(0, _prevKeyLen)).SequenceCompareTo(key) >= 0)
            throw new ArgumentException("Keys must be added in strictly ascending order.", nameof(key));

        if (_dataBlock.RecordCount > 0 && _dataBlock.WouldExceedIfAdded(key.Length, value.Length, SortedTable.BlockSize))
            FlushDataBlock(key);

        _dataBlock.Add(key, value);
        key.CopyTo(_prevKey);
        _prevKeyLen = key.Length;
        _count++;
    }

    /// <summary>Emit the final data block, the index block, and the footer.</summary>
    public void Build()
    {
        if (_dataBlock.RecordCount > 0) FlushDataBlock(nextFirstKey: default);

        // The index block begins right after the last (unpadded) data block; record its offset so the
        // reader can locate it directly without recomputing it from the block count.
        long indexOffset = _writer.Written - _tableStart;
        _indexBlock.Finish(ref _writer, Block.FlagIndex);

        Span<byte> footer = _writer.GetSpan(SortedTable.FooterSize);
        BinaryPrimitives.WriteInt64LittleEndian(footer, _count);
        BinaryPrimitives.WriteInt64LittleEndian(footer[sizeof(long)..], _blockNumber);
        BinaryPrimitives.WriteInt64LittleEndian(footer[(2 * sizeof(long))..], indexOffset);
        footer[3 * sizeof(long)] = (byte)_restartInterval;
        footer[3 * sizeof(long) + 1] = SortedTable.FormatVersion;
        _writer.Advance(SortedTable.FooterSize);
    }

    /// <summary>Emit the current data block (4 KiB-padding it unless it is the final block) and record
    /// its separator → block number in the index. The separator is the shortest key in
    /// <c>[lastKey, nextFirstKey)</c>; the final block (<paramref name="nextFirstKey"/> empty) uses its
    /// own last key.</summary>
    private void FlushDataBlock(scoped ReadOnlySpan<byte> nextFirstKey)
    {
        _dataBlock.Finish(ref _writer, Block.FlagBlock);
        bool isLast = nextFirstKey.IsEmpty;
        if (!isLast) PadZeros((-(_writer.Written - _tableStart)) & (SortedTable.BlockSize - 1));

        Span<byte> sepBuf = stackalloc byte[256];
        ReadOnlySpan<byte> lastKey = _prevKey.AsSpan(0, _prevKeyLen);
        int sepLen;
        if (isLast)
        {
            lastKey.CopyTo(sepBuf);
            sepLen = _prevKeyLen;
        }
        else
        {
            sepLen = FindShortestSeparator(lastKey, nextFirstKey, sepBuf);
        }

        Span<byte> blockNumBuf = stackalloc byte[SortedTable.IndexValueSize];
        BinaryPrimitives.WriteUInt32LittleEndian(blockNumBuf, checked((uint)_blockNumber));
        _indexBlock.Add(sepBuf[..sepLen], blockNumBuf);
        _blockNumber++;
        _dataBlock.Reset();
    }

    private void PadZeros(long count)
    {
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, 256);
            _writer.GetSpan(chunk)[..chunk].Clear();
            _writer.Advance(chunk);
            count -= chunk;
        }
    }

    /// <summary>Shortest key <c>S</c> with <paramref name="a"/> ≤ <c>S</c> &lt; <paramref name="b"/>
    /// (caller guarantees <paramref name="a"/> &lt; <paramref name="b"/>), written to
    /// <paramref name="dst"/>; returns its length. Falls back to <paramref name="a"/> when it cannot be
    /// shortened.</summary>
    private static int FindShortestSeparator(scoped ReadOnlySpan<byte> a, scoped ReadOnlySpan<byte> b, scoped Span<byte> dst)
    {
        int min = Math.Min(a.Length, b.Length);
        int l = 0;
        while (l < min && a[l] == b[l]) l++;
        if (l < min && a[l] + 1 < b[l])
        {
            a[..l].CopyTo(dst);
            dst[l] = (byte)(a[l] + 1);
            return l + 1;
        }
        a.CopyTo(dst);
        return a.Length;
    }

    public void Dispose()
    {
        _dataBlock.Dispose();
        _indexBlock.Dispose();
    }
}
