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
/// Builds a single-level <see cref="SortedTable"/>. Records are streamed to the writer in
/// arbitrary <see cref="Add"/> order; the keys (and their record offsets) are buffered off-heap
/// and sorted once at <see cref="Build"/>, which then appends the ascending offset region and the
/// footer. Buffering every key in memory is the deliberate "unoptimized" cost — see
/// <see cref="SortedTable"/>. Wire layout there too.
/// </summary>
/// <remarks>
/// Decoupling on-disk order from <see cref="Add"/> order lets the snapshot builder emit records
/// in whatever order is convenient (e.g. computing the metadata <c>blob_range</c> only after every
/// trie RLP has been written) without reordering its blob writes.
/// </remarks>
internal ref struct SortedTableBuilder<TWriter> where TWriter : IByteBufferWriter
{
    // Per-record bookkeeping: where the record landed in the writer (relative to the table start)
    // and where its key bytes sit in _keyBuf, so Build can sort by key without re-reading the writer.
    private struct Entry
    {
        public uint RecordOffset;
        public int KeyOffset;
        public int KeyLength;
    }

    private ref TWriter _writer;
    private readonly long _tableStart;
    private readonly NativeMemoryList<byte> _keyBuf;
    private readonly NativeMemoryList<Entry> _entries;

    public SortedTableBuilder(ref TWriter writer, int expectedKeyCount = 16)
    {
        _writer = ref writer;
        _tableStart = writer.Written;
        _entries = new NativeMemoryList<Entry>(Math.Max(1, expectedKeyCount));
        _keyBuf = new NativeMemoryList<byte>(Math.Max(16, expectedKeyCount * 24));
    }

    /// <summary>Append one record. Keys must be unique; callers feed each materialized key once.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        uint recordOffset = checked((uint)(_writer.Written - _tableStart));

        WriteUInt16(checked((ushort)key.Length));
        IByteBufferWriter.Copy(ref _writer, key);
        WriteUInt16(checked((ushort)value.Length));
        IByteBufferWriter.Copy(ref _writer, value);

        int keyOffset = _keyBuf.Count;
        _keyBuf.AddRange(key);
        _entries.Add(new Entry { RecordOffset = recordOffset, KeyOffset = keyOffset, KeyLength = key.Length });
    }

    /// <summary>Sort the buffered keys ascending, then emit the offset region and footer.</summary>
    public unsafe void Build()
    {
        Span<Entry> entries = _entries.AsSpan();
        if (entries.Length > 0)
        {
            byte* keyBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(_keyBuf.AsSpan()));
            _entries.Sort(new KeyComparer(keyBase));
        }

        for (int i = 0; i < entries.Length; i++)
        {
            Span<byte> dst = _writer.GetSpan(SortedTable.OffsetSize);
            BinaryPrimitives.WriteUInt32LittleEndian(dst, entries[i].RecordOffset);
            _writer.Advance(SortedTable.OffsetSize);
        }

        Span<byte> footer = _writer.GetSpan(SortedTable.FooterSize);
        BinaryPrimitives.WriteInt64LittleEndian(footer, entries.Length);
        footer[sizeof(long)] = SortedTable.FormatVersion;
        _writer.Advance(SortedTable.FooterSize);
    }

    private void WriteUInt16(ushort value)
    {
        Span<byte> dst = _writer.GetSpan(SortedTable.SizePrefix);
        BinaryPrimitives.WriteUInt16LittleEndian(dst, value);
        _writer.Advance(SortedTable.SizePrefix);
    }

    public void Dispose()
    {
        _keyBuf.Dispose();
        _entries.Dispose();
    }

    /// <summary>Compares two entries by their key bytes (ascending) read from the stable
    /// native key buffer base pointer captured at <see cref="Build"/> time.</summary>
    private readonly unsafe struct KeyComparer(byte* keyBase) : IComparer<Entry>
    {
        public int Compare(Entry a, Entry b) =>
            new ReadOnlySpan<byte>(keyBase + a.KeyOffset, a.KeyLength)
                .SequenceCompareTo(new ReadOnlySpan<byte>(keyBase + b.KeyOffset, b.KeyLength));
    }
}
