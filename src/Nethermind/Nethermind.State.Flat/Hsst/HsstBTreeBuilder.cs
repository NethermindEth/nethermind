// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries.
/// Entries MUST be added in sorted key order. No internal sorting is performed.
///
/// Binary layout (BTree):
///   [Data Region: entries...][Index Region: B-tree nodes...][IndexType: u8 = 0x01]
///   Root index is readable from the end via MetadataLength byte (no trailer).
///
/// Entry format (normal, value first, lengths forward-readable from MetadataStart):
///   [Value][ValueLength: LEB128][KeyLength: u8][FullKey]
/// MetadataStart points at the ValueLength LEB128. KeyLength is a single byte: keys are
/// capped at 255 bytes by format contract. The leaf B-tree node also stores a separator
/// (a min-length prefix of the full key) for binary-search navigation, but the
/// data-region entry is self-describing — the full key lives in the entry tail and the
/// reader does not need to consult the leaf to recover it. (ValueLength uses LEB128
/// because values are unbounded; the LEB128 terminator chain is forward-readable only,
/// so the lengths sit after the value and the index aims at them.)
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
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish value write. Computes length from snapshot taken by BeginValueWrite.
    /// Key must be greater than previous key (sorted order).
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);

        int actualLen = checked((int)(_writer.Written - _writtenBeforeValue));
        // metadataPos is relative to the data section start (== _baseOffset).
        // The index builder reads keys back through OpenReader using these positions.
        long metadataPos = _writer.Written - _baseOffset;

        // Write [ValueLength: LEB128][KeyLength: u8][FullKey]. The full key lives in
        // the data region so the entry is self-describing; the leaf separator stored
        // in the B-tree node is recomputed at Build() time from the flushed bytes.
        Span<byte> leb = _writer.GetSpan(5);
        int lebLen = Leb128.Write(leb, 0, actualLen);
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
    /// Build index, then append the trailing IndexType byte. The ref writer is already advanced.
    /// The root index node is readable from the end via its MetadataLength byte; the IndexType
    /// byte sits one byte further out, at the very end of the HSST.
    /// </summary>
    public void Build()
    {
        int maxLeafEntries = _options.MaxLeafEntries;
        int minLeafEntries = Math.Min(_options.MinLeafEntries, maxLeafEntries);
        int maxIntermediateEntries = _options.MaxIntermediateEntries;
        int maxIntermediateBytes = _options.MaxIntermediateBytes;

        long dataSectionSize = _writer.Written - _baseOffset;
        long absoluteIndexStart = dataSectionSize;
        TReader reader = _writer.OpenReader(dataSectionSize);
        try
        {
            HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
                ref _writer, reader, _entryPositions.AsSpan(), _options.MinSeparatorLength);

            indexBuilder.Build(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries, maxIntermediateBytes);
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

        // Trailing IndexType byte (last byte of the HSST).
        Span<byte> tail = _writer.GetSpan(1);
        tail[0] = (byte)IndexType.BTree;
        _writer.Advance(1);
    }
}
