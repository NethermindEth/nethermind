// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries.
/// Entries MUST be added in sorted key order. No internal sorting is performed.
///
/// Binary layout (BTree):
///   [Data Region: entries...][Index Region: B-tree nodes...][RootSize: u16 LE][IndexType: u8 = 0x01]
///   The root node's start is computed as (HSST end - 3 - RootSize); its header sits at that
///   first byte. Per-node fields run header → keys → values (low → high) so a forward read of
///   the metadata pulls the keys/values into cache via the hardware prefetcher.
///
/// Entry format (normal, value first, lengths forward-readable from MetadataStart):
///   [optional pad][Value][ValueLength: LEB128][KeyLength: u8][FullKey]
/// MetadataStart points at the ValueLength LEB128. KeyLength is a single byte: keys are
/// capped at 255 bytes by format contract. The leaf B-tree node also stores a separator
/// (a min-length prefix of the full key) for binary-search navigation, but the
/// data-region entry is self-describing — the full key lives in the entry tail and the
/// reader does not need to consult the leaf to recover it. (ValueLength uses LEB128
/// because values are unbounded; the LEB128 terminator chain is forward-readable only,
/// so the lengths sit after the value and the index aims at them.)
/// The reader recovers the value via ValueStart = MetadataStart - ValueLength, so any
/// leading pad bytes a caller inserts between BeginValueWrite and the real value (e.g.
/// to keep the value within a 4 KiB page) are inert gap data — no index entry points at
/// them. Use the <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.FinishValueWrite(System.ReadOnlySpan{byte},int)"/>
/// overload to declare the real value length when padding has been inserted.
///
/// Memory: while the data section is being written, the only per-key state held in
/// memory is one <c>long</c> per entry (the metadata position). Separators and the
/// previous key are not buffered — at <see cref="Build"/> time the index builder is
/// handed a reader over the just-written data section and recomputes separators
/// on-demand from the flushed bytes.
/// </summary>
public ref struct HsstBTreeBuilder<TWriter, TReader, TPin>
    where TWriter : IByteBufferWriterWithReader<TReader, TPin>
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    private ref TWriter _writer;
    private long _writtenBeforeValue;
    private readonly long _baseOffset;
    private readonly HsstBTreeOptions _options;

    // Per-key metadata position relative to the data section start. Replaces the
    // (separator buffer, HsstEntry triple, prev key buffer) state held by the
    // pre-OpenReader builder.
    private NativeMemoryListRef<long> _entryPositions;

    /// <summary>
    /// Create builder writing via the given writer.
    /// The trailing IndexType byte is appended in <see cref="Build"/>.
    /// Allocates working buffers from NativeMemory — call Dispose() to free them.
    /// <paramref name="expectedKeyCount"/> sizes the entry-positions buffer up front;
    /// pass an estimate when known to avoid resize allocations. The buffer still grows on demand.
    /// </summary>
    public HsstBTreeBuilder(ref TWriter writer, HsstBTreeOptions? options = null, int expectedKeyCount = 16)
    {
        HsstBTreeOptions opts = options ?? HsstBTreeOptions.Default;

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _options = opts;

        _entryPositions = new NativeMemoryListRef<long>(expectedKeyCount);
    }

    /// <summary>
    /// Free working NativeMemory buffer.
    /// </summary>
    public void Dispose() => _entryPositions.Dispose();

    /// <summary>
    /// Begin writing a value. Returns ref to the shared writer and snapshots Written.
    /// After writing, call FinishValueWrite with just the key.
    ///
    /// Callers may advance the writer past leading padding bytes before writing the
    /// real value bytes — e.g. to keep the value from crossing a 4 KiB page
    /// boundary — and then close the entry with the padding-aware overload
    /// <see cref="FinishValueWrite(ReadOnlySpan{byte}, long)"/>. Padding sits between
    /// the BeginValueWrite snapshot and (Written - valueLength); the reader recovers
    /// the value via ValueStart = MetadataStart - ValueLength, so leading pad bytes
    /// are inert gap data that no index entry points at.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish value write. Computes length from snapshot taken by BeginValueWrite —
    /// every byte written since BeginValueWrite is treated as part of the value.
    /// Use <see cref="FinishValueWrite(ReadOnlySpan{byte}, long)"/> to declare a
    /// value length smaller than the writer delta when leading padding was inserted.
    /// Key must be greater than previous key (sorted order).
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key)
    {
        long actualLen = _writer.Written - _writtenBeforeValue;
        FinishValueWrite(key, actualLen);
    }

    /// <summary>
    /// Finish value write with an explicit value length. The writer may have been
    /// advanced past <paramref name="valueLength"/> bytes — any leading bytes
    /// between the BeginValueWrite snapshot and (Written - valueLength) are treated
    /// as padding and become inert gap data that no index entry points at. Use this
    /// to keep a value from crossing a 4 KiB page boundary by padding ahead of it.
    /// Key must be greater than previous key (sorted order).
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key, long valueLength)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);
        ArgumentOutOfRangeException.ThrowIfNegative(valueLength);
        Debug.Assert(
            valueLength <= _writer.Written - _writtenBeforeValue,
            "valueLength exceeds bytes written since BeginValueWrite");

        // metadataPos is relative to the data section start (== _baseOffset).
        // The index builder reads keys back through OpenReader using these positions.
        long metadataPos = _writer.Written - _baseOffset;

        // Write [ValueLength: LEB128][KeyLength: u8][FullKey]. The full key lives in
        // the data region so the entry is self-describing; the leaf separator stored
        // in the B-tree node is recomputed at Build() time from the flushed bytes.
        // 64-bit LEB128 takes up to 10 bytes.
        Span<byte> leb = _writer.GetSpan(10);
        int lebLen = Leb128.Write(leb, 0, valueLength);
        _writer.Advance(lebLen);

        Span<byte> kl = _writer.GetSpan(1);
        kl[0] = (byte)key.Length;
        _writer.Advance(1);

        if (key.Length > 0)
        {
            IByteBufferWriter.Copy(ref _writer, key);
        }

        _entryPositions.Add(metadataPos);
    }

    /// <summary>
    /// Convenience: add key-value pair in one call.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);
        _writtenBeforeValue = _writer.Written;
        IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(key);
    }

    /// <summary>
    /// Build index, then append the trailing [RootSize u16 LE][IndexType u8] (3 bytes).
    /// Reader locates the root via (HSST end - 3 - RootSize). A node is capped at 64 KiB
    /// so RootSize fits in u16.
    /// </summary>
    public void Build()
    {
        int maxLeafEntries = _options.MaxLeafEntries;
        int minLeafEntries = Math.Min(_options.MinLeafEntries, maxLeafEntries);
        int maxIntermediateEntries = _options.MaxIntermediateEntries;
        int maxIntermediateBytes = _options.MaxIntermediateBytes;
        int minIntermediateChildren = Math.Min(_options.MinIntermediateChildren, maxIntermediateEntries);
        int minIntermediateBytes = Math.Min(_options.MinIntermediateBytes, maxIntermediateBytes);

        long dataSectionSize = _writer.Written - _baseOffset;
        long absoluteIndexStart = dataSectionSize;
        int rootSize;
        TReader reader = _writer.OpenReader(dataSectionSize);
        try
        {
            HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
                ref _writer, reader, _entryPositions.AsSpan(), _options.MinSeparatorLength);

            rootSize = indexBuilder.Build(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries, maxIntermediateBytes, minIntermediateChildren, minIntermediateBytes);
        }
        finally
        {
            // Release the data-section view eagerly. The writer can outlive this Build()
            // call and host further HSSTs whose data sections will need to OpenReader on
            // the same writer; the single-reader-at-a-time contract requires the prior
            // view to be released first. On Linux this also applies MADV_DONTNEED to the
            // just-swept range right when sweeping ends, instead of waiting until the
            // writer itself is disposed.
            _writer.DisposeActiveReader();
        }

        if ((uint)rootSize > ushort.MaxValue)
            throw new InvalidOperationException($"Root node size {rootSize} exceeds u16 trailer field");

        // Trailing [RootSize u16 LE][IndexType u8]; IndexType is the last byte of the HSST.
        Span<byte> tail = _writer.GetSpan(3);
        tail[0] = (byte)rootSize;
        tail[1] = (byte)(rootSize >> 8);
        tail[2] = (byte)IndexType.BTree;
        _writer.Advance(3);
    }
}
