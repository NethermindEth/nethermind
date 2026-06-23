// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Builds a <see cref="SortedTable"/>. Records are buffered off-heap as they are <see cref="Add"/>ed
/// (in arbitrary order), then at <see cref="Build"/> sorted by key and written as a run of
/// 4 KiB-aligned data blocks plus a single index block (separator → block number) and a footer.
/// </summary>
/// <remarks>
/// Both the data blocks and the index reuse <see cref="BlockBuilder"/>. Each finished data block is
/// zero-padded to <see cref="SortedTable.BlockSize"/> so block <c>i</c> sits at <c>i·BlockSize</c> and
/// is addressed by block number. The index entry for a block is the shortest separator between that
/// block's last key and the next block's first key (the last block uses its own last key). Only the
/// current data block and the index are buffered during <see cref="Build"/>.
/// </remarks>
internal ref struct SortedTableBuilder<TWriter> where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private readonly long _tableStart;
    private readonly int _restartInterval;
    // Records in insertion order, each [ks u8][key][vs u8][value]; _entries holds the start offset
    // of each record within _recordBuf, sorted by key at Build.
    private readonly NativeMemoryList<byte> _recordBuf;
    private readonly NativeMemoryList<int> _entries;

    public SortedTableBuilder(ref TWriter writer, int expectedKeyCount = 16, int restartInterval = SortedTable.DefaultRestartInterval)
    {
        _writer = ref writer;
        _tableStart = writer.Written;
        _restartInterval = restartInterval;
        _entries = new NativeMemoryList<int>(Math.Max(1, expectedKeyCount));
        _recordBuf = new NativeMemoryList<byte>(Math.Max(32, expectedKeyCount * 32));
    }

    /// <summary>Buffer one record. Keys must be unique; key and value lengths must each be ≤ 255.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        _entries.Add(_recordBuf.Count);
        Span<byte> hdr = stackalloc byte[1];
        hdr[0] = checked((byte)key.Length);
        _recordBuf.AddRange(hdr);
        _recordBuf.AddRange(key);
        hdr[0] = checked((byte)value.Length);
        _recordBuf.AddRange(hdr);
        _recordBuf.AddRange(value);
    }

    /// <summary>Sort the buffered records by key and emit the data blocks, the index block, and the footer.</summary>
    public unsafe void Build()
    {
        Span<int> entries = _entries.AsSpan();
        Span<byte> records = _recordBuf.AsSpan();
        if (entries.Length > 0)
        {
            // Sort only reorders _entries; _recordBuf is never mutated here, so recordBase stays valid
            // for the whole sort. Do not Add to _recordBuf inside the comparator.
            byte* recordBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(records));
            _entries.Sort(new KeyComparer(recordBase));
        }

        using BlockBuilder dataBlock = new(_restartInterval, SortedTable.BlockSize);
        using BlockBuilder indexBlock = new(_restartInterval);

        Span<byte> prevKey = stackalloc byte[256]; // last key added to the current data block
        int prevKeyLen = 0;
        Span<byte> sepBuf = stackalloc byte[256];
        Span<byte> blockNumBuf = stackalloc byte[SortedTable.IndexValueSize];
        long blockNumber = 0;
        int lastBlockSize = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            int off = entries[i];
            int ks = records[off];
            ReadOnlySpan<byte> key = records.Slice(off + Block.SizePrefix, ks);
            int vsOff = off + Block.SizePrefix + ks;
            int vs = records[vsOff];
            ReadOnlySpan<byte> value = records.Slice(vsOff + Block.SizePrefix, vs);

            if (dataBlock.RecordCount > 0 && dataBlock.WouldExceedIfAdded(ks, vs, SortedTable.BlockSize))
            {
                FlushDataBlock(dataBlock, indexBlock, prevKey[..prevKeyLen], key, blockNumber, sepBuf, blockNumBuf, isLast: false);
                blockNumber++;
                dataBlock.Reset();
            }

            dataBlock.Add(key, value);
            key.CopyTo(prevKey);
            prevKeyLen = ks;
        }

        if (dataBlock.RecordCount > 0)
        {
            lastBlockSize = (int)FlushDataBlock(dataBlock, indexBlock, prevKey[..prevKeyLen], default, blockNumber, sepBuf, blockNumBuf, isLast: true);
            blockNumber++;
        }

        // The index block begins right after the last (unpadded) data block.
        indexBlock.Finish(ref _writer);

        Span<byte> footer = _writer.GetSpan(SortedTable.FooterSize);
        BinaryPrimitives.WriteInt64LittleEndian(footer, entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[sizeof(long)..], checked((uint)blockNumber));
        BinaryPrimitives.WriteUInt16LittleEndian(footer[(sizeof(long) + sizeof(uint))..], checked((ushort)lastBlockSize));
        footer[sizeof(long) + sizeof(uint) + sizeof(ushort)] = (byte)_restartInterval;
        footer[sizeof(long) + sizeof(uint) + sizeof(ushort) + 1] = SortedTable.FormatVersion;
        _writer.Advance(SortedTable.FooterSize);
    }

    /// <summary>Emit the current data block (4 KiB-padding it unless it is the final block) and record
    /// its separator → block number in the index. The separator is the shortest key in
    /// <c>[lastKey, nextFirstKey)</c>; the final block (<paramref name="isLast"/>) uses its own last key.
    /// Returns the block's unpadded content size.</summary>
    private long FlushDataBlock(BlockBuilder dataBlock, BlockBuilder indexBlock,
        scoped ReadOnlySpan<byte> lastKey, scoped ReadOnlySpan<byte> nextFirstKey, long blockNumber,
        scoped Span<byte> sepBuf, scoped Span<byte> blockNumBuf, bool isLast)
    {
        long blockSize = dataBlock.Finish(ref _writer);
        if (!isLast) PadZeros((-(_writer.Written - _tableStart)) & (SortedTable.BlockSize - 1));

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
        BinaryPrimitives.WriteUInt32LittleEndian(blockNumBuf, checked((uint)blockNumber));
        indexBlock.Add(sepBuf[..sepLen], blockNumBuf);
        return blockSize;
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
        _recordBuf.Dispose();
        _entries.Dispose();
    }

    /// <summary>Compares two records by their inline key bytes (ascending), read from the stable
    /// native record-buffer base pointer captured at <see cref="Build"/> time.</summary>
    private readonly unsafe struct KeyComparer(byte* recordBase) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            ReadOnlySpan<byte> ka = new(recordBase + a + Block.SizePrefix, recordBase[a]);
            ReadOnlySpan<byte> kb = new(recordBase + b + Block.SizePrefix, recordBase[b]);
            return ka.SequenceCompareTo(kb);
        }
    }
}
