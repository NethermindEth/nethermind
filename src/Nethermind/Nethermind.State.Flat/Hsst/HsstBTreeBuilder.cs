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

    // Writer's page index (writer.Written / PageLayout.PageSize) at the last
    // observation point. Used by MaybeFlushBeforeEntry to gate the
    // FlushPendingNotOnCurrentPage call — entries can only become stranded on a
    // prior page when the writer's own page index has advanced, and Add() is the
    // only path that mutates the writer between consecutive Adds, so the gate is
    // safe.
    private long _lastWriterPage;

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
        _lastWriterPage = (_writer.Written - _writer.FirstOffset) / PageLayout.PageSize;
        PrimePerAddBuffers(ref _ownedBuffers, expectedKeyCount, keyLength);
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
        _lastWriterPage = (_writer.Written - _writer.FirstOffset) / PageLayout.PageSize;
        PrimePerAddBuffers(ref buffers, expectedKeyCount, keyLength);
    }

    /// <summary>
    /// Reserve <c>CommonPrefixArr</c> at <c>max(expectedKeyCount, 64)</c> bytes and,
    /// when <paramref name="keyLength"/> is known, <c>PrevKeyBuf</c> at <c>keyLength</c>
    /// bytes. The per-<c>Add</c> hot path then reads these slots with a tight bounds
    /// check (and a cold grow helper for <c>CommonPrefixArr</c>) instead of the
    /// <c>oldArr is null || oldArr.Length &lt; entryIdx + 1</c> branch on every entry.
    /// When <paramref name="keyLength"/> is <c>-1</c> at construction (deferred), the
    /// <c>PrevKeyBuf</c> rent is delegated to the first <c>OnEntryAdded</c> that
    /// learns the length.
    /// </summary>
    private static void PrimePerAddBuffers(ref HsstBTreeBuilderBuffers buffers, int expectedKeyCount, int keyLength)
    {
        int cpCap = Math.Max(expectedKeyCount, 64);
        HsstBTreeBuilderBuffers.EnsureSize(ref buffers.CommonPrefixArr, cpCap);
        if (keyLength > 0)
            HsstBTreeBuilderBuffers.EnsureSize(ref buffers.PrevKeyBuf, keyLength);
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
    private ref NativeMemoryListRef<byte> PendingKeys
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Buffers.PendingKeys;
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
        // Trigger 1: close out any pending entries before the streaming value
        // starts flowing. The streaming bytes will straddle pages, so flushing now
        // keeps any pending leaf colocated with its entries. Prune stranded pending
        // first (key on a prior page) so the leaf only covers entries that share
        // the writer's current page. A singleton pending set is pushed onto
        // CurrentLevel as a direct Entry descriptor (see EmitInlineLeaf's singleton
        // fast path) — the common all-streaming case where every entry becomes its
        // own direct-Entry child of the intermediate level above.
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        if (bufs.EntryPositions.Count > _pendingFirstEntryIdx)
        {
            FlushPendingNotOnCurrentPage();
            if (bufs.EntryPositions.Count > _pendingFirstEntryIdx)
                EmitInlineLeaf();
        }
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
        => FinishValueWrite(key, valueLength, -1);

    /// <summary>
    /// Same as <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long)"/>, but accepts
    /// a precomputed LCP byte count against <c>Buffers.PrevKeyBuf</c> (or <c>-1</c> when
    /// unknown). Used by <see cref="AddCore"/> to forward the LCP already computed by
    /// <see cref="MaybeFlushBeforeEntry"/>; the streaming
    /// <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long)"/> path passes <c>-1</c>.
    /// </summary>
    private void FinishValueWrite(scoped ReadOnlySpan<byte> key, long valueLength, int precomputedLcp)
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

        // Single GetSpan/Advance for the post-value [FlagByte][LEB128][FullKey] trailer.
        // Value bytes were streamed in via the caller's BeginValueWrite snapshot and are
        // already on the writer; this trailer is bounded by 1 + 10 + key.Length.
        int lebSize = Leb128.EncodedSize(valueLength);
        int trailerLen = 1 + lebSize + key.Length;
        Span<byte> dest = _writer.GetSpan(trailerLen);
        dest[0] = (byte)BSearchNodeKind.Entry;
        Leb128.Write(dest, 1, valueLength);
        if (key.Length > 0) key.CopyTo(dest.Slice(1 + lebSize, key.Length));
        _writer.Advance(trailerLen);

        EmitEntryBookkeeping(ref Buffers, key, metadataPos, precomputedLcp);
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
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        // +1 for the leading per-entry flag byte.
        int lebSize = Leb128.EncodedSize((long)value.Length);
        long entryLen = 1L + key.Length + lebSize + value.Length;
        int lcp = MaybeFlushBeforeEntry(ref bufs, key, entryLen);
        TryAlign(entryLen); // best-effort; entry lands unaligned if false
        AddCore(ref bufs, key, value, lebSize, lcp);
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
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        // +1 for the leading per-entry flag byte.
        int lebSize = Leb128.EncodedSize((long)value.Length);
        long entryLen = 1L + key.Length + lebSize + value.Length;
        int lcp = MaybeFlushBeforeEntry(ref bufs, key, entryLen);
        if (!TryAlign(entryLen)) return false;
        AddCore(ref bufs, key, value, lebSize, lcp);
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
    /// public method pays double page-math. <paramref name="precomputedLcp"/> is
    /// the raw LCP byte count returned by <see cref="MaybeFlushBeforeEntry"/>
    /// (<c>-1</c> if unknown) and is forwarded into
    /// <see cref="OnEntryAdded(System.ReadOnlySpan{byte},int)"/> so the per-key
    /// LCP loop runs once per buffered <see cref="Add"/>.
    /// </summary>
    private void AddCore(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value, int lebSize, int precomputedLcp)
    {
        if (_keyLength < 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);
            _keyLength = key.Length;
        }
        else if (key.Length != _keyLength)
            throw new ArgumentException($"key length {key.Length} != declared keyLength {_keyLength}", nameof(key));

        // Single GetSpan + Advance per entry. Pre-pad has already run via TryAlign in
        // the caller; the reserved slice starts at the post-pad writer position. Entry
        // bytes are laid down via local offsets into <c>dest</c>, then a single
        // <c>Advance(totalLen)</c> commits the whole record at once. Avoids the
        // four-touch GetSpan/Advance dance of the legacy path (flag, Copy(key/value),
        // LEB128, Copy(remaining)).
        int totalLen = 1 + key.Length + lebSize + value.Length;
        long entryStart = _writer.Written - _baseOffset;
        Span<byte> dest = _writer.GetSpan(totalLen);

        long entryPos;
        if (_keyFirst)
        {
            // Entry layout: [FlagByte=Entry][FullKey][LEB128 ValueLength][Value]. EntryStart =
            // FlagByte position; the BTree reader's dispatch loop reads the flag byte first
            // to recognize the entry, then walks forward past the key + LEB128 to the value.
            dest[0] = (byte)BSearchNodeKind.Entry;
            int off = 1;
            if (key.Length > 0) key.CopyTo(dest.Slice(off, key.Length));
            off += key.Length;
            Leb128.Write(dest, off, (long)value.Length);
            off += lebSize;
            if (value.Length > 0) value.CopyTo(dest.Slice(off, value.Length));
            entryPos = entryStart;
        }
        else
        {
            // Entry layout: [Value][FlagByte=Entry][LEB128 ValueLength][FullKey]. MetadataStart
            // = the FlagByte position (== entryStart + value.Length, expressed relative to the
            // data-section start at _baseOffset); the BTree reader recovers ValueStart from
            // MetadataStart - ValueLength.
            int off = 0;
            if (value.Length > 0) value.CopyTo(dest.Slice(off, value.Length));
            off += value.Length;
            long metadataPos = entryStart + value.Length;
            dest[off] = (byte)BSearchNodeKind.Entry;
            off++;
            Leb128.Write(dest, off, (long)value.Length);
            off += lebSize;
            if (key.Length > 0) key.CopyTo(dest.Slice(off, key.Length));
            entryPos = metadataPos;
        }
        _writer.Advance(totalLen);

        EmitEntryBookkeeping(ref bufs, key, entryPos, precomputedLcp);
    }

    /// <summary>
    /// Per-entry list pushes + LCP update shared by the buffered <see cref="AddCore"/>
    /// path and the streaming <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long,int)"/>
    /// path. Records the entry's index pointer (MetadataStart in key-after-value
    /// mode, EntryStart in key-first mode), appends the key to the pending leaf set,
    /// and runs the LCP / PendingMaxSepLen / PrevKeyBuf bookkeeping in
    /// <see cref="OnEntryAdded"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitEntryBookkeeping(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, long entryPos, int precomputedLcp)
    {
        bufs.EntryPositions.Add(entryPos);
        if (key.Length > 0) bufs.PendingKeys.AddRange(key);
        OnEntryAdded(ref bufs, key, precomputedLcp);
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

        // Trigger 3: flush any remaining unflushed entries so HsstIndexBuilder.Build
        // can skip its leaf phase entirely. Prune stranded pending first so the final
        // flush only covers entries on the writer's current page; any older entries
        // become direct Entry children of the intermediate level instead.
        //
        // Single-entry HSST short-circuit: when the build holds exactly one entry,
        // bypass FlushPendingNotOnCurrentPage and emit it as a 1-entry inline leaf
        // via forceLeaf:true. Two failure modes are prevented:
        //   1. A page-crossing value would push the lone entry past the writer's
        //      page, FlushPendingNotOnCurrentPage would strand it as a direct Entry
        //      descriptor on CurrentLevel.
        //   2. EmitInlineLeaf's own singleton fast path would route through
        //      FlushPendingAsEntries and also produce a direct Entry descriptor.
        // Either way HsstIndexBuilder.Build's currentNative.Count == 1 early-return
        // would mis-report rootSize as the entry record's full byte length
        // (1 + keyLen + LEB128 + valueLen) — unbounded, overflowing the u16 trailer
        // for large values. forceLeaf:true forces the leaf wrap so the lone
        // descriptor on CurrentLevel is a bounded leaf node.
        if (EntryPositions.Count == 1)
        {
            EmitInlineLeaf(forceLeaf: true);
        }
        else if (EntryPositions.Count > _pendingFirstEntryIdx)
        {
            FlushPendingNotOnCurrentPage();
            if (EntryPositions.Count > _pendingFirstEntryIdx)
                EmitInlineLeaf();
        }

        long dataSectionSize = _writer.Written - _baseOffset;
        long absoluteIndexStart = dataSectionSize;
        int rootSize;
        int rootPrefixLen;
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;

        // No data-section reader needed: every descriptor in <c>CurrentLevel</c> carries
        // its first-entry full key in the parallel <c>CurrentLevelFirstKeys</c> list,
        // populated at descriptor-push time (EmitInlineLeaf, FlushPendingAsEntries,
        // FlushPendingNotOnCurrentPage). HsstIndexBuilder.Build propagates first-keys as it
        // walks up the tree, so no read-back is required.
        HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
            ref _writer, bufs.EntryPositions.AsSpan(), _keyLength, ref bufs);
        rootSize = indexBuilder.Build(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries, maxIntermediateBytes, minIntermediateChildren, minIntermediateBytes);
        rootPrefixLen = indexBuilder.RootPrefixLen;

        if ((uint)rootSize > ushort.MaxValue)
            throw new InvalidOperationException($"Root node size {rootSize} exceeds u16 trailer field");
        if ((uint)rootPrefixLen > byte.MaxValue)
            throw new InvalidOperationException($"Root prefix length {rootPrefixLen} exceeds u8 trailer field");

        // Trailing layout: [RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8].
        // IndexType is the last byte of the HSST. Empty builds (_keyLength still -1
        // because no Add() / FinishValueWrite was called) record KeyLength = 0 and
        // RootPrefixLen = 0; the reader never decodes any keys in that case.
        // CopyRootPrefixBytes writes the prefix bytes directly into the head of the
        // trailer span — no intermediate buffer needed.
        int trailerKeyLength = _keyLength < 0 ? 0 : _keyLength;
        int trailerLen = 5 + rootPrefixLen;
        Span<byte> tail = _writer.GetSpan(trailerLen);
        if (rootPrefixLen > 0) indexBuilder.CopyRootPrefixBytes(tail[..rootPrefixLen]);
        tail[rootPrefixLen] = (byte)rootPrefixLen;
        tail[rootPrefixLen + 1] = (byte)rootSize;
        tail[rootPrefixLen + 2] = (byte)(rootSize >> 8);
        tail[rootPrefixLen + 3] = (byte)trailerKeyLength;
        tail[rootPrefixLen + 4] = (byte)(_keyFirst ? IndexType.BTreeKeyFirst : IndexType.BTree);
        _writer.Advance(trailerLen);
    }

    /// <summary>
    /// Per-entry bookkeeping: record the new entry's LCP against the previous entry's
    /// key in <c>Buffers.CommonPrefixArr</c>, then refresh <c>Buffers.PrevKeyBuf</c>
    /// for the next add. Forwarder for the streaming <see cref="FinishValueWrite"/>
    /// path that has no precomputed LCP.
    /// </summary>
    private void OnEntryAdded(scoped ReadOnlySpan<byte> key) => OnEntryAdded(ref Buffers, key, -1);

    /// <summary>
    /// Same as <see cref="OnEntryAdded(System.ReadOnlySpan{byte})"/>, but accepts the
    /// raw LCP byte count against <c>Buffers.PrevKeyBuf</c> already computed by
    /// <see cref="MaybeFlushBeforeEntry"/>. Pass <c>-1</c> when no precomputed value
    /// is available; the method then walks the prev/current keys itself.
    /// <paramref name="bufs"/> is the same ref the caller already resolved at the
    /// top of <see cref="Add"/> / <see cref="BeginValueWrite"/>; threading it
    /// through avoids re-resolving the <see cref="Buffers"/> branch on every Add.
    /// </summary>
    private void OnEntryAdded(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, int precomputedLcp)
    {
        int entryIdx = bufs.EntryPositions.Count - 1;
        byte[]? prevKey = bufs.PrevKeyBuf;
        int cp = 0;
        if (entryIdx > 0 && _keyLength > 0 && prevKey is not null)
        {
            cp = precomputedLcp >= 0
                ? precomputedLcp
                : MemoryExtensions.CommonPrefixLength(prevKey.AsSpan(0, Math.Min(prevKey.Length, _keyLength)), key);
        }
        // CommonPrefixArr was primed at construction to max(expectedKeyCount, 64) bytes
        // and grows monotonically. Hot path: tight bounds check + direct write. Cold
        // path: out-of-line helper preserves the bytes already written for entries
        // 0..entryIdx before swapping in the larger pool array.
        byte[] cpArr = bufs.CommonPrefixArr!;
        if ((uint)entryIdx >= (uint)cpArr.Length)
        {
            cpArr = GrowCommonPrefixArr(ref bufs, entryIdx + 1);
        }
        cpArr[entryIdx] = (byte)cp;

        // Incremental update of PendingMaxSepLen so MaybeFlushBeforeEntry can skip
        // its O(pending) scan. Mirrors the loop it replaces: sepLen for an entry is
        // min(cp + 1, keyLength), and we want the max over the pending range. The
        // first-in-pending entry (entryIdx == _pendingFirstEntryIdx) contributes too —
        // matching today's scan which iterates from _pendingFirstEntryIdx inclusive.
        if (_keyLength > 0)
        {
            byte sl = (byte)Math.Min(cp + 1, _keyLength);
            if (sl > bufs.PendingMaxSepLen) bufs.PendingMaxSepLen = sl;
        }

        // Refresh PrevKeyBuf for the next entry's LCP. The buffer is sized to
        // <c>_keyLength</c> by the constructor (when known) or here on the first
        // entry of a deferred-keyLength build; after that, every Add writes
        // exactly _keyLength bytes into a buffer that is already large enough.
        if (_keyLength > 0 && key.Length == _keyLength)
        {
            byte[]? prev = bufs.PrevKeyBuf;
            if (prev is null || prev.Length < _keyLength)
            {
                HsstBTreeBuilderBuffers.EnsureSize(ref bufs.PrevKeyBuf, _keyLength);
                prev = bufs.PrevKeyBuf;
            }
            key.CopyTo(prev);
        }
    }

    /// <summary>
    /// Out-of-line grow path for <c>CommonPrefixArr</c>. Rents a larger pool array,
    /// copies the bytes already written for entries <c>0..entryIdx-1</c> (which the
    /// caller's hot loop has populated incrementally), returns the old array to the
    /// pool, and assigns the new one. Returns the new array so the caller can
    /// continue writing without re-reading the field.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] GrowCommonPrefixArr(ref HsstBTreeBuilderBuffers bufs, int needed)
    {
        byte[]? oldArr = bufs.CommonPrefixArr;
        byte[] newArr = System.Buffers.ArrayPool<byte>.Shared.Rent(needed);
        if (oldArr is not null)
        {
            Array.Copy(oldArr, newArr, oldArr.Length);
            System.Buffers.ArrayPool<byte>.Shared.Return(oldArr);
        }
        bufs.CommonPrefixArr = newArr;
        return newArr;
    }

    /// <summary>
    /// Trigger 2 (page-boundary fit). Called before each entry write. Estimates the
    /// size of a page-local leaf describing the current pending set plus this new
    /// entry; if writing the entry plus that leaf would push past the current 4 KiB
    /// page boundary, flush the pending set as a leaf now and start a fresh page
    /// for the new entry.
    /// </summary>
    /// <returns>
    /// The raw LCP byte count between <paramref name="key"/> and
    /// <c>Buffers.PrevKeyBuf</c>, or <c>-1</c> when no meaningful LCP exists
    /// (short key, zero <c>_keyLength</c>, or <c>PrevKeyBuf</c> not yet populated).
    /// The caller threads this through <see cref="AddCore"/> into
    /// <see cref="OnEntryAdded(System.ReadOnlySpan{byte},int)"/> so the per-key
    /// LCP loop runs once per buffered <see cref="Add"/>/<see cref="TryAddAligned"/>.
    /// </returns>
    private int MaybeFlushBeforeEntry(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, long entryLen)
    {
        // Compute LCP once at the top; reused for the leaf-fit estimate below and
        // returned for the caller to forward into OnEntryAdded. Uses PrevKeyBuf
        // (set by the last OnEntryAdded) — survives leaf flushes that clear
        // PendingKeys, and stays valid even when the prior entry was stranded
        // onto the previous page and direct-flushed.
        byte[]? prevKey = bufs.PrevKeyBuf;
        int lcp = -1;
        if (_keyLength > 0 && key.Length == _keyLength && prevKey is not null)
        {
            lcp = MemoryExtensions.CommonPrefixLength(prevKey.AsSpan(0, _keyLength), key);
        }

        int pending = bufs.EntryPositions.Count - _pendingFirstEntryIdx;
        if (pending < 1) return lcp;
        if (_keyLength <= 0) return lcp;

        // Stranded-entry prune is only meaningful when the writer's page index
        // has advanced since the last Add. Add() is the only thing that mutates
        // the writer between Adds, so a cached _lastWriterPage is sufficient.
        // FlushPendingNotOnCurrentPage updates _lastWriterPage internally.
        long writerPage = (_writer.Written - _writer.FirstOffset) / PageLayout.PageSize;
        if (writerPage != _lastWriterPage)
        {
            FlushPendingNotOnCurrentPage();
            pending = bufs.EntryPositions.Count - _pendingFirstEntryIdx;
            if (pending < 1) return lcp;
        }

        int newSepLen = lcp >= 0 ? Math.Min(lcp + 1, _keyLength) : _keyLength;

        // Max sep length over pending entries is maintained incrementally by
        // OnEntryAdded (and rebuilt by FlushPendingNotOnCurrentPage's
        // partial-flush rescan).
        int maxSepLen = bufs.PendingMaxSepLen;
        int maxSepWithNew = Math.Max(maxSepLen, newSepLen);

        // Leaf-size upper bound matching the Variable-key layout written by
        // BSearchIndexWriter: 12-byte header + 4 bytes/entry (u16 prefixArr +
        // u16 offsetArr) + 2 bytes/entry value slot + per-entry tail bytes
        // beyond the 2-byte prefix slot (so max(0, sepLen - 2)). Safe upper
        // bound; tighter than the legacy formula that double-counted the
        // 2-byte prefix.
        int estLeafTailPer = Math.Max(0, maxSepWithNew - 2);
        int estLeafPerEntry = 4 + PageLocalLeafValueSlotBytes + estLeafTailPer;
        int estLeaf = PageLocalLeafHeaderBytes + (pending + 1) * estLeafPerEntry;

        long inPage = (_writer.Written - _writer.FirstOffset) & PageLayout.PageMask;
        long remaining = PageLayout.PageSize - inPage;
        if (entryLen + estLeaf <= remaining) return lcp;

        // Doesn't fit on the current page. Seal pending now and start fresh for
        // the new entry. A multi-entry pending set goes out as a page-local leaf;
        // a singleton goes out as a direct Entry descriptor via EmitInlineLeaf's
        // singleton fast path (no leaf header + slot bytes spent on a degenerate
        // 1-entry node).
        // Edge case: the K-entry leaf itself may not fit (e.g., the previous entry
        // was close to PageSize, leaving remaining < estLeafActual). Writing a
        // cross-page leaf would spend a header + per-entry slot bytes on a node
        // that loses the page-locality it exists to provide. Instead push each
        // pending entry directly onto the next index level — the future
        // intermediate node will point at the entries, saving the leaf entirely.
        //
        // No force-pad to the next page after the flush: the leaf-fit check above
        // plus the page-prune at the top of MaybeFlushBeforeEntry (and at every
        // other flush site) already handle the K=1 trap. If the next entry slips
        // into the post-leaf slack, the next iteration's leaf-fit check will see
        // remaining < estLeafActual and direct-flush the trapped entry instead
        // of writing a cross-page 1-entry leaf.
        int estLeafActualTailPer = Math.Max(0, maxSepLen - 2);
        int estLeafActualPerEntry = 4 + PageLocalLeafValueSlotBytes + estLeafActualTailPer;
        int estLeafActual = PageLocalLeafHeaderBytes + pending * estLeafActualPerEntry;
        if (estLeafActual > remaining)
            FlushPendingAsEntries();
        else
            EmitInlineLeaf();

        return lcp;
    }

    private const int PageLocalLeafHeaderBytes = 12;
    private const int PageLocalLeafValueSlotBytes = 2;

    /// <summary>
    /// Write a page-local leaf node into the data region for the entries in the range
    /// <c>[_pendingFirstEntryIdx, EntryPositions.Count)</c>, push a descriptor onto
    /// <c>Buffers.CurrentLevel</c>, and advance <see cref="_pendingFirstEntryIdx"/>.
    /// No-op when nothing is pending.
    /// </summary>
    /// <remarks>
    /// Singleton fast path: when exactly one entry is pending, the leaf wrap is pure
    /// overhead (12-byte header + per-entry slot + tail key bytes) — the lone entry
    /// is instead pushed onto <c>CurrentLevel</c> as an
    /// <see cref="BSearchNodeKind.Entry"/>-kind descriptor via
    /// <see cref="FlushPendingAsEntries"/>. The intermediate node above dispatches
    /// on the flag byte and handles Entry / Leaf / Intermediate children uniformly.
    /// Callers that need the leaf wrap even for a singleton (i.e. the lone entry
    /// would otherwise become the root, where a direct Entry would inflate rootSize
    /// past the u16 trailer field) must pass <paramref name="forceLeaf"/> = true.
    /// </remarks>
    private void EmitInlineLeaf(bool forceLeaf = false)
    {
        int firstEntryIdx = _pendingFirstEntryIdx;
        int count = EntryPositions.Count - firstEntryIdx;
        if (count == 0) return;

        // Singleton short-circuit: route through FlushPendingAsEntries so the lone
        // entry becomes a direct Entry descriptor instead of a degenerate 1-entry
        // leaf. Bypassed when forceLeaf is set (single-entry-HSST case in Build()).
        if (count == 1 && !forceLeaf)
        {
            FlushPendingAsEntries();
            return;
        }

        long nodeStart = _writer.Written - _baseOffset;

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.ValueScratch, Math.Max(64, count * (2 + 8)));

        // Wrap each pending entry in a single-entry descriptor and feed to the unified
        // WriteIndexNode. Each child is an entry record (NodeKind=Entry, no header), so
        // its PrefixLen is zero — no prefix bytes to recover from the parent's slot at
        // descent time.
        Span<HsstIndexNodeInfo> children = stackalloc HsstIndexNodeInfo[count];
        ReadOnlySpan<long> entryPositions = bufs.EntryPositions.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int entryIdx = firstEntryIdx + i;
            children[i] = new HsstIndexNodeInfo(entryPositions[entryIdx], entryIdx, entryIdx, prefixLen: 0);
        }

        // Per-child first-keys for WriteIndexNode: each pending entry's full key sits in
        // PendingKeys at offset i * _keyLength.
        ReadOnlySpan<byte> childFirstKeys = bufs.PendingKeys.AsSpan();

        HsstIndexBuilder<TWriter, TReader, TPin> indexBuilder = new(
            ref _writer, entryPositions, _keyLength, ref bufs);
        indexBuilder.WriteIndexNode(children, childFirstKeys, bufs.ValueScratch!, bufs.CommonPrefixArr!, out int leafPrefixLen);

        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(nodeStart, firstEntryIdx, firstEntryIdx + count - 1, leafPrefixLen));
        // The new leaf's first-key = entry firstEntryIdx's full key, which is the first
        // _keyLength bytes of PendingKeys. Push it into CurrentLevelFirstKeys before
        // PendingKeys is cleared so intermediate construction can read it later.
        if (_keyLength > 0) bufs.CurrentLevelFirstKeys.AddRange(bufs.PendingKeys.AsSpan()[.._keyLength]);
        _pendingFirstEntryIdx = EntryPositions.Count;
        // Drop the in-flight keys now that they've been folded into a leaf. The leaf's
        // first-key survives in CurrentLevelFirstKeys; subsequent adds repopulate
        // PendingKeys with the next pending set.
        bufs.PendingKeys.Clear();
        // Pending range is empty — reset the incremental max-sep tracker.
        bufs.PendingMaxSepLen = 0;
    }

    /// <summary>
    /// Push each pending entry directly onto <c>Buffers.CurrentLevel</c> as an
    /// <see cref="BSearchNodeKind.Entry"/>-kind descriptor, skipping the leaf
    /// node entirely. Used by <see cref="MaybeFlushBeforeEntry"/> when the
    /// would-be leaf for the pending entries wouldn't fit on the current page:
    /// rather than write a cross-page leaf that loses its locality benefit,
    /// let the future intermediate node point at the entries directly. The
    /// reader's flag-byte dispatch handles a mix of Entry/Leaf/Intermediate
    /// children under an intermediate uniformly. Bookkeeping (advancing
    /// <see cref="_pendingFirstEntryIdx"/>, clearing PendingKeys) mirrors
    /// <see cref="EmitInlineLeaf"/>.
    /// </summary>
    private void FlushPendingAsEntries()
    {
        int firstEntryIdx = _pendingFirstEntryIdx;
        int count = EntryPositions.Count - firstEntryIdx;
        if (count == 0) return;

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        ReadOnlySpan<long> entryPositions = bufs.EntryPositions.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int entryIdx = firstEntryIdx + i;
            bufs.CurrentLevel.Add(new HsstIndexNodeInfo(
                entryPositions[entryIdx], entryIdx, entryIdx, prefixLen: 0));
        }
        // Each direct-flushed entry is one descriptor in CurrentLevel; copy every
        // pending key (count * _keyLength bytes, the entire current PendingKeys
        // payload) into CurrentLevelFirstKeys in matching order before PendingKeys
        // is cleared so intermediate construction can read them later.
        if (_keyLength > 0) bufs.CurrentLevelFirstKeys.AddRange(bufs.PendingKeys.AsSpan());

        _pendingFirstEntryIdx = EntryPositions.Count;
        bufs.PendingKeys.Clear();
        // Pending range is empty — reset the incremental max-sep tracker.
        bufs.PendingMaxSepLen = 0;
    }

    /// <summary>
    /// Direct-flush any pending entry whose flag byte (= the key region) is
    /// stranded on a page prior to the writer's current page. These entries
    /// can't share a page-local leaf with anything on the writer's current
    /// page, so push them as <see cref="BSearchNodeKind.Entry"/>-kind
    /// descriptors onto <c>Buffers.CurrentLevel</c>; the intermediate node
    /// above will point at them directly via the reader's uniform flag-byte
    /// dispatch.
    ///
    /// Entries are written with monotonically increasing positions, so the
    /// stranded entries form a contiguous prefix of pending — once the scan
    /// finds one on the writer's current page, every later one is too.
    /// </summary>
    private void FlushPendingNotOnCurrentPage()
    {
        int pending = EntryPositions.Count - _pendingFirstEntryIdx;
        if (pending == 0)
        {
            // Even when there's nothing pending to prune, the caller paths
            // (BeginValueWrite, Build, and MaybeFlushBeforeEntry's now-gated
            // path) rely on _lastWriterPage being current after this method
            // returns so the next per-Add gate check is a single cmp.
            _lastWriterPage = (_writer.Written - _writer.FirstOffset) / PageLayout.PageSize;
            return;
        }

        long firstOffset = _writer.FirstOffset;
        long writerPage = (_writer.Written - firstOffset) / PageLayout.PageSize;
        _lastWriterPage = writerPage;

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        ReadOnlySpan<long> entryPositions = bufs.EntryPositions.AsSpan();

        int firstOnCurrent = _pendingFirstEntryIdx;
        while (firstOnCurrent < EntryPositions.Count)
        {
            long flagAbs = entryPositions[firstOnCurrent] + _baseOffset;
            long flagPage = (flagAbs - firstOffset) / PageLayout.PageSize;
            if (flagPage == writerPage) break;
            firstOnCurrent++;
        }

        int directCount = firstOnCurrent - _pendingFirstEntryIdx;
        if (directCount == 0) return;

        for (int i = 0; i < directCount; i++)
        {
            int entryIdx = _pendingFirstEntryIdx + i;
            bufs.CurrentLevel.Add(new HsstIndexNodeInfo(
                entryPositions[entryIdx], entryIdx, entryIdx, prefixLen: 0));
        }

        // Each direct-flushed entry becomes one descriptor in CurrentLevel; copy the
        // matching front slice of PendingKeys (directCount * _keyLength bytes) into
        // CurrentLevelFirstKeys before the front bytes are dropped below.
        if (_keyLength > 0)
        {
            int bytesRemoved = directCount * _keyLength;
            bufs.CurrentLevelFirstKeys.AddRange(bufs.PendingKeys.AsSpan()[..bytesRemoved]);
        }

        _pendingFirstEntryIdx = firstOnCurrent;

        // Drop the direct-flushed entries' keys from the front of PendingKeys.
        // Shift the remaining-pending keys to position 0 so PendingKeys indexing
        // (which is local-offset based) stays valid for the surviving pending set.
        if (_keyLength > 0)
        {
            int bytesRemoved = directCount * _keyLength;
            Span<byte> keysSpan = bufs.PendingKeys.AsSpan();
            keysSpan[bytesRemoved..].CopyTo(keysSpan);
            bufs.PendingKeys.Truncate(keysSpan.Length - bytesRemoved);
        }

        // Recompute PendingMaxSepLen over the surviving pending range. The
        // direct-flushed entries that contributed to the previous max are gone,
        // and the surviving entries' cp values in CommonPrefixArr are untouched
        // by the direct flush. This rescan runs at most once per writer-page
        // transition (and only when stranded entries existed); the per-Add
        // scan it replaces is gone.
        byte newMax = 0;
        if (_keyLength > 0)
        {
            byte[]? cpArr = bufs.CommonPrefixArr;
            if (cpArr is not null)
            {
                for (int i = _pendingFirstEntryIdx; i < EntryPositions.Count; i++)
                {
                    byte sl = (byte)Math.Min(cpArr[i] + 1, _keyLength);
                    if (sl > newMax) newMax = sl;
                }
            }
        }
        bufs.PendingMaxSepLen = newMax;
    }
}
