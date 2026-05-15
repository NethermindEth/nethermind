// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries.
/// Entries MUST be added in sorted key order. No internal sorting is performed.
///
/// Two data-region entry layouts are supported, selected by the <c>keyFirst</c>
/// constructor flag:
///
/// Binary layout (BTree, <c>keyFirst = false</c>; trailer <c>IndexType = 0x01</c>):
///   [Data Region: entries...][Index Region: B-tree nodes...][RootSize: u16 LE][KeyLength: u8][IndexType: u8 = 0x01]
///   The root node's start is computed as (HSST end - 4 - RootSize); its header sits at that
///   first byte. Per-node fields run header → keys → values (low → high) so a forward read of
///   the metadata pulls the keys/values into cache via the hardware prefetcher.
///
/// Entry format (key-after-value):
///   [optional pad][Value][ValueLength: LEB128][FullKey]
/// MetadataStart points at the ValueLength LEB128. Key length is invariant per HSST and
/// lives in the trailer (single byte, 0–255 by format contract), so the data-section
/// entry does not repeat it. The reader recovers the value via
/// <c>ValueStart = MetadataStart − ValueLength</c>. Leading pad bytes inserted between
/// <see cref="BeginValueWrite"/> and the real value are inert; use
/// <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long)"/> to declare the real
/// value length.
///
/// Binary layout (BTreeKeyFirst, <c>keyFirst = true</c>; trailer <c>IndexType = 0x07</c>):
///   Same overall shape, but per-entry layout is keys-first to mirror the keys-first
///   sub-slot HSST: the entry's per-entry metadata (key + length) sits at the entry's
///   front, so a forward scan crossing nested HSSTs walks key → length → value
///   throughout.
///
/// Entry format (key-first):
///   [FullKey: KeyLength bytes][ValueLength: LEB128][Value: V bytes]
/// The leaf index pointer targets <c>EntryStart</c> (FullKey byte 0). The reader walks
/// forward: <c>KeyLength</c> from the trailer locates the LEB128; the LEB128 yields the
/// value length; the value follows. Streaming writes are not supported in this mode —
/// the value length must be known when the entry is laid down, so callers must use
/// <see cref="Add(System.ReadOnlySpan{byte},System.ReadOnlySpan{byte})"/>.
///
/// Memory: while the data section is being written, the only per-key state held in
/// memory is one <c>long</c> per entry (the entry's index pointer target — MetadataStart
/// in key-after-value mode, EntryStart in key-first mode). Separators and the previous
/// key are not buffered — at <see cref="Build"/> time the index builder is handed a
/// reader over the just-written data section and recomputes separators on-demand from
/// the flushed bytes.
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
    private readonly bool _keyFirst;
    private int _keyLength;

    // Per-key metadata-position list owned by this builder in the auto-owned constructor.
    // In the buffer-borrowing constructor the equivalent list lives on the caller's
    // HsstBTreeBuilderBuffers (accessed via _externalBuffers) and _ownedEntryPositions
    // stays default.
    private NativeMemoryListRef<long> _ownedEntryPositions;

    // Pointer to the caller's HsstBTreeBuilderBuffers when constructed via the borrowed
    // overload; default(void*) for the auto-owned path. Stored as void* because
    // HsstBTreeBuilderBuffers is a ref struct and not eligible for T* / managed fields.
    private readonly unsafe void* _externalBuffers;
    private readonly bool _useExternalBuffers;

    /// <summary>
    /// Create builder writing via the given writer.
    /// The trailing [RootSize u16][KeyLength u8][IndexType u8] is appended in <see cref="Build"/>.
    /// Allocates working buffers from NativeMemory — call Dispose() to free them.
    /// <paramref name="keyLength"/> declares the fixed key length (0–255) every entry must use;
    /// all keys in a single HSST must be exactly this many bytes. Pass -1 to defer the
    /// declaration to the first <see cref="Add"/>/<see cref="FinishValueWrite(System.ReadOnlySpan{byte})"/>
    /// call, which then locks the length for the rest of the build. The fixed length is
    /// recorded once in the trailer (single KeyLength:u8 byte before the IndexType byte)
    /// rather than per-entry, and the builder rejects mismatches at build time so readers
    /// can rely on the trailer value.
    /// <paramref name="expectedKeyCount"/> sizes the entry-positions buffer up front;
    /// pass an estimate when known to avoid resize allocations. The buffer still grows on demand.
    /// When <paramref name="keyFirst"/> is true, the data-region entries are written
    /// key-first (<c>[FullKey][LEB128][Value]</c>) and the trailer carries
    /// <see cref="IndexType.BTreeKeyFirst"/>; <see cref="BeginValueWrite"/> is rejected
    /// because the value length must be known up front, so callers must use
    /// <see cref="Add"/>.
    /// </summary>
    public HsstBTreeBuilder(ref TWriter writer, int keyLength, HsstBTreeOptions? options = null, int expectedKeyCount = 16, bool keyFirst = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keyLength, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyLength, 255);

        HsstBTreeOptions opts = options ?? HsstBTreeOptions.Default;

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _options = opts;
        _keyLength = keyLength;
        _keyFirst = keyFirst;

        _ownedEntryPositions = new NativeMemoryListRef<long>(expectedKeyCount);
        _useExternalBuffers = false;
    }

    /// <summary>
    /// Create a builder that shares an externally-owned <see cref="HsstBTreeBuilderBuffers"/>
    /// across multiple builds. Use this overload when the same builder pattern fires
    /// repeatedly in a loop (per slot-prefix group, per merged address) so the work
    /// buffers — entry positions, common-prefix array, leaf-first-keys, level lists,
    /// value scratch, segment tree, DFS stack — stay rented across invocations.
    /// <paramref name="buffers"/> is reset for this build via
    /// <see cref="HsstBTreeBuilderBuffers.ResetForBuild"/>; it remains the caller's
    /// responsibility to dispose.
    /// See the primary constructor for <paramref name="keyFirst"/> semantics.
    /// </summary>
    public unsafe HsstBTreeBuilder(ref TWriter writer, scoped ref HsstBTreeBuilderBuffers buffers, int keyLength, HsstBTreeOptions? options = null, int expectedKeyCount = 16, bool keyFirst = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keyLength, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyLength, 255);

        HsstBTreeOptions opts = options ?? HsstBTreeOptions.Default;

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _options = opts;
        _keyLength = keyLength;
        _keyFirst = keyFirst;

        buffers.ResetForBuild(expectedKeyCount);
        _externalBuffers = Unsafe.AsPointer(ref buffers);
        _useExternalBuffers = true;
    }

    /// <summary>
    /// Free the working buffer when this builder owns it. In the borrowed-buffers
    /// constructor path the caller's struct owns and disposes those buffers; this is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (!_useExternalBuffers) _ownedEntryPositions.Dispose();
    }

    [UnscopedRef]
    private unsafe ref NativeMemoryListRef<long> EntryPositions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _useExternalBuffers
            ? ref Unsafe.AsRef<HsstBTreeBuilderBuffers>(_externalBuffers).EntryPositions
            : ref _ownedEntryPositions;
    }

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
    ///
    /// Not supported in key-first mode (the value length must be known when the entry
    /// is laid down). Callers in key-first mode must use <see cref="Add"/>.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        if (_keyFirst)
            throw new InvalidOperationException("Key-first BTree requires Add(key, value); BeginValueWrite/FinishValueWrite streaming is not supported.");
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish value write. Computes length from snapshot taken by BeginValueWrite —
    /// every byte written since BeginValueWrite is treated as part of the value.
    /// Use <see cref="FinishValueWrite(ReadOnlySpan{byte}, long)"/> to declare a
    /// value length smaller than the writer delta when leading padding was inserted.
    /// Key must be greater than previous key (sorted order).
    /// Not supported in key-first mode — use <see cref="Add"/>.
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
    /// Not supported in key-first mode — use <see cref="Add"/>.
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key, long valueLength)
    {
        if (_keyFirst)
            throw new InvalidOperationException("Key-first BTree requires Add(key, value); BeginValueWrite/FinishValueWrite streaming is not supported.");

        if (_keyLength < 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);
            _keyLength = key.Length;
        }
        else if (key.Length != _keyLength)
            throw new ArgumentException($"key length {key.Length} != declared keyLength {_keyLength}", nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegative(valueLength);
        Debug.Assert(
            valueLength <= _writer.Written - _writtenBeforeValue,
            "valueLength exceeds bytes written since BeginValueWrite");

        // metadataPos is relative to the data section start (== _baseOffset).
        // The index builder reads keys back through OpenReader using these positions.
        long metadataPos = _writer.Written - _baseOffset;

        // Write [ValueLength: LEB128][FullKey]. The full key lives in the data region
        // so the entry is self-describing; the leaf separator stored in the B-tree
        // node is recomputed at Build() time from the flushed bytes. Key length is
        // uniform per HSST and recorded once in the trailer, not per entry.
        // 64-bit LEB128 takes up to 10 bytes.
        Span<byte> leb = _writer.GetSpan(10);
        int lebLen = Leb128.Write(leb, 0, valueLength);
        _writer.Advance(lebLen);

        if (key.Length > 0)
        {
            IByteBufferWriter.Copy(ref _writer, key);
        }

        EntryPositions.Add(metadataPos);
    }

    /// <summary>
    /// Convenience: add key-value pair in one call.
    /// In key-after-value mode the layout written is <c>[Value][LEB128 ValueLength][FullKey]</c>
    /// and the recorded entry position aims at the LEB128 byte (MetadataStart).
    /// In key-first mode (<c>keyFirst = true</c> at construction) the layout is
    /// <c>[FullKey][LEB128 ValueLength][Value]</c> and the recorded entry position aims at
    /// FullKey byte 0 (EntryStart).
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (_keyLength < 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);
            _keyLength = key.Length;
        }
        else if (key.Length != _keyLength)
            throw new ArgumentException($"key length {key.Length} != declared keyLength {_keyLength}", nameof(key));

        if (_keyFirst)
        {
            // Entry layout: [FullKey][LEB128 ValueLength][Value]. EntryStart = FullKey byte 0.
            long entryStart = _writer.Written - _baseOffset;
            if (key.Length > 0)
                IByteBufferWriter.Copy(ref _writer, key);
            Span<byte> leb = _writer.GetSpan(10);
            int lebLen = Leb128.Write(leb, 0, value.Length);
            _writer.Advance(lebLen);
            if (value.Length > 0)
                IByteBufferWriter.Copy(ref _writer, value);
            EntryPositions.Add(entryStart);
            return;
        }

        _writtenBeforeValue = _writer.Written;
        IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(key);
    }

    /// <summary>
    /// Build index, then append the trailing [RootSize u16 LE][KeyLength u8][IndexType u8] (4 bytes).
    /// Reader locates the root via (HSST end - 4 - RootSize). A node is capped at 64 KiB
    /// so RootSize fits in u16. KeyLength is the fixed key length for every entry in this
    /// HSST (the builder enforces uniformity); 0 when the build was empty and no length
    /// was declared.
    /// </summary>
    public unsafe void Build()
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
            if (_useExternalBuffers)
            {
                ref HsstBTreeBuilderBuffers bufs = ref Unsafe.AsRef<HsstBTreeBuilderBuffers>(_externalBuffers);
                HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
                    ref _writer, reader, bufs.EntryPositions.AsSpan(), _keyLength, ref bufs, _keyFirst);
                rootSize = indexBuilder.Build(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries, maxIntermediateBytes, minIntermediateChildren, minIntermediateBytes);
            }
            else
            {
                // Auto-owned path: allocate a per-Build buffers struct on the stack with
                // identical semantics to the pre-refactor inline rentals.
                HsstBTreeBuilderBuffers localBufs = new();
                try
                {
                    HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
                        ref _writer, reader, _ownedEntryPositions.AsSpan(), _keyLength, ref localBufs, _keyFirst);
                    rootSize = indexBuilder.Build(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries, maxIntermediateBytes, minIntermediateChildren, minIntermediateBytes);
                }
                finally
                {
                    localBufs.Dispose();
                }
            }
        }
        finally
        {
            // Release the data-section view eagerly. The writer can outlive this Build()
            // call and host further HSSTs whose data sections will need to OpenReader on
            // the same writer; the single-reader-at-a-time contract requires the prior
            // view to be released first.
            _writer.DisposeActiveReader();
        }

        if ((uint)rootSize > ushort.MaxValue)
            throw new InvalidOperationException($"Root node size {rootSize} exceeds u16 trailer field");

        // Trailing [RootSize u16 LE][KeyLength u8][IndexType u8]; IndexType is the last
        // byte of the HSST. Empty builds (_keyLength still -1 because no Add() / FinishValueWrite
        // was called) record KeyLength = 0; the reader never decodes any keys in that case.
        int trailerKeyLength = _keyLength < 0 ? 0 : _keyLength;
        Span<byte> tail = _writer.GetSpan(4);
        tail[0] = (byte)rootSize;
        tail[1] = (byte)(rootSize >> 8);
        tail[2] = (byte)trailerKeyLength;
        tail[3] = (byte)(_keyFirst ? IndexType.BTreeKeyFirst : IndexType.BTree);
        _writer.Advance(4);
    }
}
