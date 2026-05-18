// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Storage;

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

    // Per-build working buffers (entry positions, full keys, per-entry LCP, current /
    // next index-build levels, value scratch, etc.). When the builder is constructed
    // via the auto-owned overload, this field is the live storage; the borrowed
    // overload leaves it default and routes through <see cref="_externalBuffers"/>
    // instead.
    private HsstBTreeBuilderBuffers _ownedBuffers;

    // Pointer to the caller's HsstBTreeBuilderBuffers when constructed via the borrowed
    // overload; default(void*) for the auto-owned path. Stored as void* because
    // HsstBTreeBuilderBuffers is a ref struct and not eligible for T* / managed fields.
    private readonly unsafe void* _externalBuffers;
    private readonly bool _useExternalBuffers;

    // Index of the first entry that has not yet been folded into a page-local leaf.
    // Add / FinishValueWrite push entries; <see cref="MaybeFlushBeforeEntry"/> closes
    // them out as an inline leaf when the page-fit estimator says the next entry
    // would push the leaf past a 4 KiB page boundary. <see cref="BeginValueWrite"/>
    // flushes on streaming-value starts, and <see cref="Build"/> does a final flush
    // of any tail entries.
    private int _pendingFirstEntryIdx;

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

        _ownedBuffers = new HsstBTreeBuilderBuffers(expectedKeyCount);
        _useExternalBuffers = false;
        _pendingFirstEntryIdx = 0;
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
        _pendingFirstEntryIdx = 0;
    }

    /// <summary>
    /// Free the working buffer when this builder owns it. In the borrowed-buffers
    /// constructor path the caller's struct owns and disposes those buffers; this is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (!_useExternalBuffers) _ownedBuffers.Dispose();
    }

    /// <summary>
    /// Reference to the active <see cref="HsstBTreeBuilderBuffers"/> — either the
    /// caller's (borrowed overload) or <see cref="_ownedBuffers"/> (auto-owned).
    /// </summary>
    [UnscopedRef]
    private unsafe ref HsstBTreeBuilderBuffers Buffers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _useExternalBuffers
            ? ref Unsafe.AsRef<HsstBTreeBuilderBuffers>(_externalBuffers)
            : ref _ownedBuffers;
    }

    [UnscopedRef]
    private ref NativeMemoryListRef<long> EntryPositions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Buffers.EntryPositions;
    }

    [UnscopedRef]
    private ref NativeMemoryListRef<byte> AllKeys
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Buffers.AllKeys;
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
        // Trigger 1: close out any pending entries as an inline leaf before the
        // streaming value starts flowing. The streaming bytes will straddle pages,
        // so flushing now keeps each pending leaf colocated with its entries.
        if (EntryPositions.Count > _pendingFirstEntryIdx)
            EmitInlineLeaf();
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

        // metadataPos is relative to the data section start (== _baseOffset). The byte at
        // this position is the entry's leading flag byte (NodeKind = Entry); the BTree
        // reader's dispatch loop reads it first to recognize the entry before decoding the
        // value/LEB128 that follow. The index builder reads keys back through OpenReader
        // using this position; both ReadKey and the leaf-floor entry decode skip the flag
        // byte before parsing the LEB128.
        long metadataPos = _writer.Written - _baseOffset;

        // Per-entry flag byte: NodeKind=Entry (0) in bits 0-1, all other bits reserved zero.
        Span<byte> flagSpan = _writer.GetSpan(1);
        flagSpan[0] = (byte)BSearchNodeKind.Entry;
        _writer.Advance(1);

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
        if (key.Length > 0) AllKeys.AddRange(key);
        OnEntryAdded(key);
    }

    /// <summary>
    /// Convenience: add key-value pair in one call. Attempts to keep the entry
    /// (key + LEB128 + value) on a single <see cref="PageLayout.PageSize"/> page
    /// via a small leading zero pad when the writer is mid-page; if the pad would
    /// exceed <see cref="PageLayout.PadThreshold"/> or the entry is larger than
    /// one page, the entry is written without alignment.
    /// In key-after-value mode the layout written is <c>[Value][LEB128 ValueLength][FullKey]</c>
    /// and the recorded entry position aims at the LEB128 byte (MetadataStart).
    /// In key-first mode (<c>keyFirst = true</c> at construction) the layout is
    /// <c>[FullKey][LEB128 ValueLength][Value]</c> and the recorded entry position aims at
    /// FullKey byte 0 (EntryStart).
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        // +1 for the leading per-entry flag byte.
        long entryLen = 1L + key.Length + Leb128.EncodedSize((long)value.Length) + value.Length;
        MaybeFlushBeforeEntry(key, entryLen);
        TryAlign(entryLen); // best-effort; entry lands unaligned if false
        AddCore(key, value);
    }

    /// <summary>
    /// Try to add an entry such that the whole entry block — the key, its LEB128
    /// value-length prefix, and the value — lands within a single
    /// <see cref="PageLayout.PageSize"/> page in the destination writer. If the
    /// current writer position would force the entry to straddle a page boundary,
    /// up to <see cref="PageLayout.PadThreshold"/> zero bytes are written ahead
    /// of the entry to push its start onto the next page. Returns true on a
    /// successful (possibly padded) add; returns false without writing anything
    /// if either of the unalignable cases applies:
    /// <list type="bullet">
    ///   <item>the entry is larger than one page (cannot fit at any offset)</item>
    ///   <item>the alignment pad would exceed <see cref="PageLayout.PadThreshold"/></item>
    /// </list>
    /// Works uniformly in both key-after-value and key-first modes — the entry's
    /// total byte count is the same in either layout (only the order differs),
    /// and the pad bytes sit before the entry's captured index position so the
    /// reader never reads them (key-after-value resolves the value via
    /// <c>ValueStart = MetadataStart − ValueLength</c> back-reference; key-first
    /// walks forward from EntryStart, which the index points at). Use this when
    /// you want a definite success/failure signal so the caller can fall back
    /// to a different code path on alignment failure; for best-effort alignment
    /// without a signal, use <see cref="Add"/>.
    /// </summary>
    public bool TryAddAligned(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        // +1 for the leading per-entry flag byte.
        long entryLen = 1L + key.Length + Leb128.EncodedSize((long)value.Length) + value.Length;
        MaybeFlushBeforeEntry(key, entryLen);
        if (!TryAlign(entryLen)) return false;
        AddCore(key, value);
        return true;
    }

    /// <summary>
    /// Shared pad-then-align helper. Returns true if the entry (length
    /// <paramref name="entryLen"/>) will fit on a single page at the post-call
    /// writer position — either because it already does (writer at boundary or
    /// remaining-in-page is enough) or because a pad &lt;=
    /// <see cref="PageLayout.PadThreshold"/> was written to advance to the next
    /// page boundary. Returns false (without writing) if the entry is larger
    /// than a page or the required pad exceeds the threshold.
    /// </summary>
    private bool TryAlign(long entryLen)
    {
        if (entryLen > PageLayout.PageSize) return false;
        long pageOff = (_writer.Written - _writer.FirstOffset) & PageLayout.PageMask;
        if (pageOff == 0 || pageOff + entryLen <= PageLayout.PageSize) return true;
        long padLen = PageLayout.PageSize - pageOff;
        if (padLen > PageLayout.PadThreshold) return false;
        int padInt = (int)padLen;
        Span<byte> pad = _writer.GetSpan(padInt);
        pad[..padInt].Clear();
        _writer.Advance(padInt);
        return true;
    }

    /// <summary>
    /// Layout-mode-agnostic entry write, without page-alignment. Called from
    /// <see cref="Add"/> after <see cref="TryAlign"/> has run its best-effort pad,
    /// and from <see cref="TryAddAligned"/> after a successful pad — so neither
    /// public method pays double page-math.
    /// </summary>
    private void AddCore(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
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
            // Entry layout: [FlagByte=Entry][FullKey][LEB128 ValueLength][Value]. EntryStart =
            // FlagByte position; the BTree reader's dispatch loop reads the flag byte first
            // to recognize the entry, then walks forward past the key + LEB128 to the value.
            long entryStart = _writer.Written - _baseOffset;
            Span<byte> flagSpan = _writer.GetSpan(1);
            flagSpan[0] = (byte)BSearchNodeKind.Entry;
            _writer.Advance(1);
            if (key.Length > 0)
                IByteBufferWriter.Copy(ref _writer, key);
            Span<byte> leb = _writer.GetSpan(10);
            int lebLen = Leb128.Write(leb, 0, value.Length);
            _writer.Advance(lebLen);
            if (value.Length > 0)
                IByteBufferWriter.Copy(ref _writer, value);
            EntryPositions.Add(entryStart);
            if (key.Length > 0) AllKeys.AddRange(key);
            OnEntryAdded(key);
            return;
        }

        _writtenBeforeValue = _writer.Written;
        IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(key);
    }

    /// <summary>
    /// Build index, then append the trailing
    /// <c>[RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8]</c>
    /// (5 + RootPrefixLen bytes). Reader locates the root via
    /// <c>HSST end − 5 − RootPrefixLen − RootSize</c> and supplies the trailer's
    /// <c>RootPrefix</c> bytes to the root node's <c>BSearchIndexReader.ReadFromStart</c>
    /// — non-root nodes get their prefix bytes from the parent's separator, but the root
    /// has no parent so the bytes ride the trailer instead. A node is capped at 64 KiB
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

        // Trigger 3: flush any remaining unflushed entries into one final inline
        // leaf, so HsstIndexBuilder.Build can skip its leaf phase entirely.
        if (EntryPositions.Count > _pendingFirstEntryIdx)
            EmitInlineLeaf();

        long dataSectionSize = _writer.Written - _baseOffset;
        long absoluteIndexStart = dataSectionSize;
        int rootSize;
        int rootPrefixLen;
        // Up to 128 prefix bytes per BSearchIndexLayoutPlanner.MaxCommonKeyPrefixLen.
        Span<byte> rootPrefixBytes = stackalloc byte[128];
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
            ref _writer, bufs.EntryPositions.AsSpan(), _keyLength, ref bufs, _keyFirst);
        rootSize = indexBuilder.Build(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries, maxIntermediateBytes, minIntermediateChildren, minIntermediateBytes);
        rootPrefixLen = indexBuilder.RootPrefixLen;
        if (rootPrefixLen > 0) indexBuilder.CopyRootPrefixBytes(rootPrefixBytes[..rootPrefixLen]);

        if ((uint)rootSize > ushort.MaxValue)
            throw new InvalidOperationException($"Root node size {rootSize} exceeds u16 trailer field");
        if ((uint)rootPrefixLen > byte.MaxValue)
            throw new InvalidOperationException($"Root prefix length {rootPrefixLen} exceeds u8 trailer field");

        // Trailing layout: [RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8].
        // IndexType is the last byte of the HSST. Empty builds (_keyLength still -1
        // because no Add() / FinishValueWrite was called) record KeyLength = 0 and
        // RootPrefixLen = 0; the reader never decodes any keys in that case.
        int trailerKeyLength = _keyLength < 0 ? 0 : _keyLength;
        int trailerLen = 5 + rootPrefixLen;
        Span<byte> tail = _writer.GetSpan(trailerLen);
        if (rootPrefixLen > 0) rootPrefixBytes[..rootPrefixLen].CopyTo(tail);
        tail[rootPrefixLen] = (byte)rootPrefixLen;
        tail[rootPrefixLen + 1] = (byte)rootSize;
        tail[rootPrefixLen + 2] = (byte)(rootSize >> 8);
        tail[rootPrefixLen + 3] = (byte)trailerKeyLength;
        tail[rootPrefixLen + 4] = (byte)(_keyFirst ? IndexType.BTreeKeyFirst : IndexType.BTree);
        _writer.Advance(trailerLen);
    }

    /// <summary>
    /// Per-entry bookkeeping: compute the new entry's LCP against the previous entry's
    /// key (stored in <see cref="AllKeys"/>), record it in <c>Buffers.CommonPrefixArr</c>,
    /// and fire the naive trigger when <see cref="NaiveLeafBatchSize"/> entries have
    /// accumulated since the last flush.
    /// </summary>
    private void OnEntryAdded(scoped ReadOnlySpan<byte> key)
    {
        int entryIdx = EntryPositions.Count - 1;
        int cp = 0;
        if (entryIdx > 0 && _keyLength > 0)
        {
            ReadOnlySpan<byte> all = AllKeys.AsSpan();
            ReadOnlySpan<byte> prev = all.Slice((entryIdx - 1) * _keyLength, _keyLength);
            int n = Math.Min(prev.Length, key.Length);
            int i = 0;
            while (i < n && prev[i] == key[i]) i++;
            cp = i;
        }
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        // Grow-preserving resize: HsstBTreeBuilderBuffers.EnsureSize returns the old
        // array to the pool unconditionally, losing its contents. We must copy the
        // accumulated cp[0..entryIdx) into the new buffer before the old one is
        // returned, otherwise WriteIndexNode reads garbage at higher entry indices.
        byte[]? oldArr = bufs.CommonPrefixArr;
        if (oldArr is null || oldArr.Length < entryIdx + 1)
        {
            byte[] newArr = System.Buffers.ArrayPool<byte>.Shared.Rent(entryIdx + 1);
            if (oldArr is not null)
            {
                Array.Copy(oldArr, newArr, oldArr.Length);
                System.Buffers.ArrayPool<byte>.Shared.Return(oldArr);
            }
            bufs.CommonPrefixArr = newArr;
        }
        bufs.CommonPrefixArr![entryIdx] = (byte)cp;
    }

    /// <summary>
    /// Trigger 2 (page-boundary fit). Called before each entry write. Estimates the
    /// size of a page-local leaf describing the current pending set plus this new
    /// entry; if writing the entry plus that leaf would push past the current 4 KiB
    /// page boundary, flush the pending set as a leaf now and start a fresh page
    /// for the new entry.
    /// </summary>
    private void MaybeFlushBeforeEntry(scoped ReadOnlySpan<byte> key, long entryLen)
    {
        int pending = EntryPositions.Count - _pendingFirstEntryIdx;
        if (pending < 1) return;
        if (_keyLength <= 0) return;

        // Compute the would-be LCP for the new entry against the previous entry's key,
        // so the max-sepLen prediction includes it.
        int newSepLen;
        if (key.Length == _keyLength && EntryPositions.Count > 0)
        {
            ReadOnlySpan<byte> all = AllKeys.AsSpan();
            ReadOnlySpan<byte> prev = all.Slice((EntryPositions.Count - 1) * _keyLength, _keyLength);
            int n = Math.Min(prev.Length, key.Length);
            int i = 0;
            while (i < n && prev[i] == key[i]) i++;
            newSepLen = Math.Min(i + 1, _keyLength);
        }
        else
        {
            newSepLen = _keyLength;
        }

        // Max sep length over pending entries (look at the LCPs we cached in
        // bufs.CommonPrefixArr — one byte per entry; sepLength = cp + 1, capped at
        // keyLength).
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        byte[]? cp = bufs.CommonPrefixArr;
        int maxSepLen = 0;
        if (cp is not null)
        {
            for (int i = _pendingFirstEntryIdx; i < EntryPositions.Count; i++)
            {
                int sl = Math.Min(cp[i] + 1, _keyLength);
                if (sl > maxSepLen) maxSepLen = sl;
            }
        }
        int maxSepWithNew = Math.Max(maxSepLen, newSepLen);

        // Conservative leaf-size estimate: Variable layout (4 bytes per entry —
        // u16 prefixArr + u16 offsetArr) plus tail-bytes bounded by maxSepLen,
        // plus a 12-byte header and a 2-byte value slot per entry.
        int estLeaf = PageLocalLeafHeaderBytes + (pending + 1) * (4 + maxSepWithNew) + (pending + 1) * PageLocalLeafValueSlotBytes;

        long inPage = (_writer.Written - _writer.FirstOffset) & PageLayout.PageMask;
        long remaining = PageLayout.PageSize - inPage;
        if (entryLen + estLeaf <= remaining) return;

        // Doesn't fit on the current page. Seal pending into a leaf now and start
        // fresh for the new entry. minPending = 1 so even a singleton becomes a
        // 1-entry leaf — keeps the on-disk tree a node-only structure for now.
        EmitInlineLeaf();
    }

    private const int PageLocalLeafHeaderBytes = 12;
    private const int PageLocalLeafValueSlotBytes = 2;

    /// <summary>
    /// Write a page-local leaf node into the data region for the entries in the range
    /// <c>[_pendingFirstEntryIdx, EntryPositions.Count)</c>, push a descriptor onto
    /// <c>Buffers.CurrentLevel</c>, and advance <see cref="_pendingFirstEntryIdx"/>.
    /// No-op when nothing is pending.
    /// </summary>
    private void EmitInlineLeaf()
    {
        int firstEntryIdx = _pendingFirstEntryIdx;
        int count = EntryPositions.Count - firstEntryIdx;
        if (count == 0) return;

        long nodeStart = _writer.Written - _baseOffset;

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.ValueScratch, Math.Max(64, count * (2 + 8)));

        // Wrap each pending entry in a single-entry descriptor and feed to the unified
        // WriteIndexNode. This is the leaf flavor of mixing leaves and intermediates
        // through one node-writer code path.
        Span<HsstIndexNodeInfo> children = stackalloc HsstIndexNodeInfo[count];
        ReadOnlySpan<long> entryPositions = bufs.EntryPositions.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int entryIdx = firstEntryIdx + i;
            children[i] = new HsstIndexNodeInfo(entryPositions[entryIdx], entryIdx, entryIdx, prefixLen: 0);
        }

        HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
            ref _writer, entryPositions, _keyLength, ref bufs, _keyFirst);
        int crossEntryLcp = indexBuilder.ComputeCrossEntryLcp(children, bufs.CommonPrefixArr!);
        indexBuilder.WriteIndexNode(children, BSearchNodeKind.Leaf, crossEntryLcp,
            bufs.ValueScratch!, bufs.CommonPrefixArr!, out int leafPrefixLen);

        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(nodeStart, firstEntryIdx, firstEntryIdx + count - 1, leafPrefixLen));
        _pendingFirstEntryIdx = EntryPositions.Count;
    }
}
