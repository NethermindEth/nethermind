// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Builds a <see cref="SortedTable"/> by streaming: records must be <see cref="Add"/>ed in strictly
/// ascending key order and are written straight into 4 KiB-aligned data blocks as they arrive — no
/// record buffer, so the table size is bounded by the data region (256 TiB) rather than by an in-memory
/// buffer. The index (separator → data-block byte offset) accrues one entry per flushed data block; at
/// <see cref="Build"/> the final data block and the single index block are emitted, followed by the footer.
/// </summary>
/// <remarks>
/// Both the data blocks and the index reuse <see cref="BlockBuilder"/>. Each finished data block but the
/// last is zero-padded to <see cref="SortedTable.BlockSize"/> so block <c>i</c> sits at <c>i·BlockSize</c>;
/// the index records its table-relative byte offset (changed-prefix coded via <see cref="BlockBuilder.AddChangedPrefixValue"/>).
/// The index block is written right after the last (unpadded) data block and located by the footer's
/// <c>indexOffset</c>. The index entry for a block is the shortest separator between that block's last key
/// and the next block's first key (the last block uses its own last key). Only the current data block and
/// the index are buffered.
/// </remarks>
internal ref struct SortedTableBuilder<TWriter> where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private readonly long _tableStart;
    private readonly BlockBuilder _dataBlock;
    private readonly BlockBuilder _indexBlock;
    // Last key Added overall — also the last key of the current data block, used to enforce ascending
    // order and to derive the separator when a block flushes. Keys are ≤ 255 bytes.
    private readonly NativeMemoryList<byte> _prevKey;
    // Records Added so far; only its non-zero-ness is consulted, to skip the ascending check on the first.
    private long _count;

    public SortedTableBuilder(ref TWriter writer, int restartInterval = SortedTable.DefaultRestartInterval)
    {
        _writer = ref writer;
        _tableStart = writer.Written;
        _dataBlock = new BlockBuilder(restartInterval, SortedTable.BlockSize);
        _indexBlock = new BlockBuilder(restartInterval);
        _prevKey = new NativeMemoryList<byte>(256);
    }

    /// <summary>Stream one record. Keys must arrive in strictly ascending order and be unique; key and
    /// value lengths must each be ≤ 255.</summary>
    /// <exception cref="ArgumentException">The key is not strictly greater than the previous key.</exception>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (_count > 0 && ((ReadOnlySpan<byte>)_prevKey.AsSpan()).SequenceCompareTo(key) >= 0)
            throw new ArgumentException("Keys must be added in strictly ascending order.", nameof(key));

        if (_dataBlock.RecordCount > 0 && _dataBlock.WouldCrossPage(key.Length, value.Length))
            FlushDataBlock(key);

        _dataBlock.Add(key, value);
        _prevKey.Clear();
        _prevKey.AddRange(key);
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
        BinaryPrimitives.WriteInt64LittleEndian(footer, indexOffset);
        footer[sizeof(long)] = SortedTable.FormatVersion;
        _writer.Advance(SortedTable.FooterSize);
    }

    /// <summary>Emit the current data block (4 KiB-padding it unless it is the final block) and record
    /// its separator → table-relative byte offset in the index. The separator is the shortest key in
    /// <c>[lastKey, nextFirstKey)</c>; the final block (<paramref name="nextFirstKey"/> empty) uses its
    /// own last key.</summary>
    private void FlushDataBlock(scoped ReadOnlySpan<byte> nextFirstKey)
    {
        // The data block is written here, so its table-relative start is the current writer position.
        long blockOffset = _writer.Written - _tableStart;
        _dataBlock.Finish(ref _writer, Block.FlagBlock);
        bool isLast = nextFirstKey.IsEmpty;
        if (!isLast) PadZeros((-(_writer.Written - _tableStart)) & (SortedTable.BlockSize - 1));

        Span<byte> sepBuf = stackalloc byte[256];
        ReadOnlySpan<byte> lastKey = _prevKey.AsSpan();
        int sepLen;
        if (isLast)
        {
            lastKey.CopyTo(sepBuf);
            sepLen = lastKey.Length;
        }
        else
        {
            sepLen = FindShortestSeparator(lastKey, nextFirstKey, sepBuf);
        }

        _indexBlock.AddChangedPrefixValue(sepBuf[..sepLen], blockOffset);
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
        _prevKey.Dispose();
    }
}
