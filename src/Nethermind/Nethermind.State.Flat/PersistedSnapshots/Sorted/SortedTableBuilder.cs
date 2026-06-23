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
/// Builds a single-level <see cref="SortedTable"/>. Records are buffered off-heap as they are
/// <see cref="Add"/>ed (in arbitrary order), then at <see cref="Build"/> sorted by key and written
/// to the destination <em>in sorted, contiguous order</em> with front-coded keys (block-start keys
/// stored in full), followed by a sparse offset region (one entry per
/// <see cref="SortedTable.BlockSize"/> records) and the footer.
/// </summary>
/// <remarks>
/// Physically sorting the records is what lets the offset index be sparse: a lookup binary searches
/// the sparse offsets to a block, then sequentially scans that block's records. Buffering records
/// also decouples on-disk order from <see cref="Add"/> order, so the snapshot builder can emit in
/// any convenient order (e.g. computing the metadata <c>blob_range</c> only after all trie RLP is
/// written). Values are small, so buffering them is cheap; the per-record index is one <c>int</c>.
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

    /// <summary>Sort the buffered records by key and emit the sorted records, the sparse offset
    /// region, and the footer.</summary>
    public unsafe void Build()
    {
        Span<int> entries = _entries.AsSpan();
        Span<byte> records = _recordBuf.AsSpan();
        if (entries.Length > 0)
        {
            byte* recordBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(records));
            _entries.Sort(new KeyComparer(recordBase));
        }

        long blockCount = (entries.Length + SortedTable.BlockSize - 1) / SortedTable.BlockSize;
        using NativeMemoryList<uint> blockOffsets = new((int)Math.Max(1, blockCount));

        // Front-code keys against the previous record's key, resetting (cp = 0, full key) at every
        // block start so each block — entered via its sparse offset — decodes standalone.
        Span<byte> prevKey = stackalloc byte[256];
        int prevKeyLen = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            int off = entries[i];
            int ks = records[off];
            ReadOnlySpan<byte> key = records.Slice(off + SortedTable.SizePrefix, ks);
            int vsOff = off + SortedTable.SizePrefix + ks;
            int vs = records[vsOff];
            ReadOnlySpan<byte> value = records.Slice(vsOff + SortedTable.SizePrefix, vs);

            int cp;
            if (i % SortedTable.BlockSize == 0)
            {
                blockOffsets.Add(checked((uint)(_writer.Written - _tableStart)));
                cp = 0;
            }
            else
            {
                ReadOnlySpan<byte> prev = prevKey[..prevKeyLen];
                cp = prev.CommonPrefixLength(key);
            }

            Span<byte> hdr = _writer.GetSpan(2);
            hdr[0] = (byte)cp;
            hdr[1] = (byte)(ks - cp);
            _writer.Advance(2);
            IByteBufferWriter.Copy(ref _writer, key[cp..]);
            Span<byte> vsHdr = _writer.GetSpan(SortedTable.SizePrefix);
            vsHdr[0] = (byte)vs;
            _writer.Advance(SortedTable.SizePrefix);
            IByteBufferWriter.Copy(ref _writer, value);

            key.CopyTo(prevKey);
            prevKeyLen = ks;
        }

        Span<uint> blocks = blockOffsets.AsSpan();
        for (int b = 0; b < blocks.Length; b++)
        {
            Span<byte> dst = _writer.GetSpan(SortedTable.OffsetSize);
            BinaryPrimitives.WriteUInt32LittleEndian(dst, blocks[b]);
            _writer.Advance(SortedTable.OffsetSize);
        }

        Span<byte> footer = _writer.GetSpan(SortedTable.FooterSize);
        BinaryPrimitives.WriteInt64LittleEndian(footer, entries.Length);
        footer[sizeof(long)] = (byte)SortedTable.BlockSize;
        footer[sizeof(long) + 1] = SortedTable.FormatVersion;
        _writer.Advance(SortedTable.FooterSize);
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
