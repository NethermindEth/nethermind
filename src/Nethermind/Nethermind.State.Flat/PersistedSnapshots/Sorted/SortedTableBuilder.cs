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
/// Builds a two-level <see cref="SortedTable"/>. Records are buffered off-heap as they are
/// <see cref="Add"/>ed (in arbitrary order), then at <see cref="Build"/> sorted by key and written
/// to the destination <em>in sorted, contiguous order</em> as <see cref="SortedTable.BlockSizeTarget"/>-bounded
/// data blocks (front-coded keys, per-block restart table), followed by the separator-key index and
/// the footer.
/// </summary>
/// <remarks>
/// Physically sorting the records is what lets the index be sparse: a lookup binary searches the
/// separators to a block, binary searches that block's restarts, then scans one restart run.
/// Buffering records also decouples on-disk order from <see cref="Add"/> order, so the snapshot
/// builder can emit in any convenient order (e.g. computing the metadata <c>blob_range</c> only after
/// all trie RLP is written). Only the current block's packed records and the (small) tail index are
/// buffered during <see cref="Build"/>; finished blocks stream straight to the writer.
/// </remarks>
internal ref struct SortedTableBuilder<TWriter> where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private readonly long _tableStart;
    // Records in insertion order, each [ks u8][key][vs u8][value]; _entries holds the start offset
    // of each record within _recordBuf, sorted by key at Build.
    private readonly NativeMemoryList<byte> _recordBuf;
    private readonly NativeMemoryList<int> _entries;

    public SortedTableBuilder(ref TWriter writer, int expectedKeyCount = 16)
    {
        _writer = ref writer;
        _tableStart = writer.Written;
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

    /// <summary>Sort the buffered records by key and emit the data blocks, the separator index, and
    /// the footer.</summary>
    public unsafe void Build()
    {
        Span<int> entries = _entries.AsSpan();
        Span<byte> records = _recordBuf.AsSpan();
        if (entries.Length > 0)
        {
            byte* recordBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(records));
            _entries.Sort(new KeyComparer(recordBase));
        }

        // Tail index, accumulated as blocks flush and written after all data blocks.
        using NativeMemoryList<byte> separators = new(Math.Max(16, entries.Length));     // [sepLen u8][sep] × M
        using NativeMemoryList<uint> sepEntryOffsets = new(8);                           // offset within separators of each entry
        using NativeMemoryList<uint> blockDataOffsets = new(8);                          // table-relative start of each block

        // Reusable per-block scratch — the block's packed records and its restart offsets within them.
        using NativeMemoryList<byte> blockBody = new(SortedTable.BlockSizeTarget + 512);
        using NativeMemoryList<ushort> restarts = new(64);

        Span<byte> prevKey = stackalloc byte[256]; // last key packed into the current block (cp basis + separator basis)
        int prevKeyLen = 0;
        int recordsInBlock = 0;
        Span<byte> hdr = stackalloc byte[2];

        for (int i = 0; i < entries.Length; i++)
        {
            int off = entries[i];
            int ks = records[off];
            ReadOnlySpan<byte> key = records.Slice(off + SortedTable.SizePrefix, ks);
            int vsOff = off + SortedTable.SizePrefix + ks;
            int vs = records[vsOff];
            ReadOnlySpan<byte> value = records.Slice(vsOff + SortedTable.SizePrefix, vs);

            bool opensRestart = recordsInBlock % SortedTable.RestartInterval == 0;

            // Close the current block before it would exceed the target (worst-case record, cp = 0).
            if (recordsInBlock > 0)
            {
                int header = (restarts.Count + (opensRestart ? 1 : 0) + 1) * SortedTable.RestartOffsetSize;
                int recordMax = 2 + ks + SortedTable.SizePrefix + vs;
                if (header + blockBody.Count + recordMax > SortedTable.BlockSizeTarget)
                {
                    FlushBlock(blockBody, restarts, separators, sepEntryOffsets, blockDataOffsets, prevKey[..prevKeyLen], key, isLast: false);
                    recordsInBlock = 0;
                    opensRestart = true;
                }
            }

            int cp;
            if (opensRestart)
            {
                restarts.Add(checked((ushort)blockBody.Count));
                cp = 0;
            }
            else
            {
                cp = ((ReadOnlySpan<byte>)prevKey[..prevKeyLen]).CommonPrefixLength(key);
            }

            hdr[0] = (byte)cp;
            hdr[1] = (byte)(ks - cp);
            blockBody.AddRange(hdr);
            blockBody.AddRange(key[cp..]);
            hdr[0] = (byte)vs;
            blockBody.AddRange(hdr[..1]);
            blockBody.AddRange(value);

            key.CopyTo(prevKey);
            prevKeyLen = ks;
            recordsInBlock++;
        }

        if (recordsInBlock > 0)
            FlushBlock(blockBody, restarts, separators, sepEntryOffsets, blockDataOffsets, prevKey[..prevKeyLen], default, isLast: true);

        // Separators region, then the two fixed-width offset arrays the footer locates by block count.
        long sepRegionStart = _writer.Written - _tableStart;
        IByteBufferWriter.Copy(ref _writer, separators.AsSpan());

        Span<uint> seo = sepEntryOffsets.AsSpan();
        for (int k = 0; k < seo.Length; k++)
            WriteUInt32(checked((uint)(sepRegionStart + seo[k])));

        Span<uint> bdo = blockDataOffsets.AsSpan();
        for (int k = 0; k < bdo.Length; k++)
            WriteUInt32(bdo[k]);
        WriteUInt32(checked((uint)sepRegionStart)); // sentinel: separators-region start = end of data

        Span<byte> footer = _writer.GetSpan(SortedTable.FooterSize);
        BinaryPrimitives.WriteInt64LittleEndian(footer, entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[sizeof(long)..], checked((uint)blockDataOffsets.Count));
        footer[sizeof(long) + sizeof(uint)] = (byte)SortedTable.RestartInterval;
        footer[sizeof(long) + sizeof(uint) + 1] = SortedTable.FormatVersion;
        _writer.Advance(SortedTable.FooterSize);
    }

    /// <summary>Prepend the restart table, stream the buffered block, and record its data offset and
    /// separator. The separator is the shortest key in <c>[lastKey, nextFirstKey)</c>; the final block
    /// (<paramref name="isLast"/>) uses its own last key. Clears the per-block scratch.</summary>
    private void FlushBlock(
        NativeMemoryList<byte> blockBody, NativeMemoryList<ushort> restarts,
        NativeMemoryList<byte> separators, NativeMemoryList<uint> sepEntryOffsets, NativeMemoryList<uint> blockDataOffsets,
        scoped ReadOnlySpan<byte> lastKey, scoped ReadOnlySpan<byte> nextFirstKey, bool isLast)
    {
        int n = restarts.Count;
        int headerSize = (n + 1) * SortedTable.RestartOffsetSize; // [numRestarts u16] + n restart offsets

        blockDataOffsets.Add(checked((uint)(_writer.Written - _tableStart)));

        Span<byte> num = _writer.GetSpan(SortedTable.RestartOffsetSize);
        BinaryPrimitives.WriteUInt16LittleEndian(num, checked((ushort)n));
        _writer.Advance(SortedTable.RestartOffsetSize);
        Span<ushort> rs = restarts.AsSpan();
        for (int k = 0; k < n; k++)
        {
            Span<byte> dst = _writer.GetSpan(SortedTable.RestartOffsetSize);
            BinaryPrimitives.WriteUInt16LittleEndian(dst, checked((ushort)(headerSize + rs[k])));
            _writer.Advance(SortedTable.RestartOffsetSize);
        }
        IByteBufferWriter.Copy(ref _writer, blockBody.AsSpan());

        Span<byte> sepBuf = stackalloc byte[256];
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
        sepEntryOffsets.Add(checked((uint)separators.Count));
        Span<byte> sl = stackalloc byte[1];
        sl[0] = (byte)sepLen;
        separators.AddRange(sl);
        separators.AddRange(sepBuf[..sepLen]);

        blockBody.Clear();
        restarts.Clear();
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> dst = _writer.GetSpan(SortedTable.IndexOffsetSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dst, value);
        _writer.Advance(SortedTable.IndexOffsetSize);
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
            ReadOnlySpan<byte> ka = new(recordBase + a + SortedTable.SizePrefix, recordBase[a]);
            ReadOnlySpan<byte> kb = new(recordBase + b + SortedTable.SizePrefix, recordBase[b]);
            return ka.SequenceCompareTo(kb);
        }
    }
}
