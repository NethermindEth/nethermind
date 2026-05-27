// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

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

    // Ref to the caller's HsstBTreeBuilderBuffers when constructed via the borrowed
    // overload; default (invalid) for the auto-owned path — guard with _useExternalBuffers.
    // HsstBTreeBuilder is a ref struct so a ref field is allowed; HsstBTreeBuilderBuffers
    // is no longer a ref struct so CS9050 doesn't apply.
    private readonly ref HsstBTreeBuilderBuffers _externalBuffers;
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
    public HsstBTreeBuilder(ref TWriter writer, ref HsstBTreeBuilderBuffers buffers, int keyLength, HsstBTreeOptions? options = null, int expectedKeyCount = 16, bool keyFirst = false)
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
        _externalBuffers = ref buffers;
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
    private ref HsstBTreeBuilderBuffers Buffers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _useExternalBuffers ? ref _externalBuffers : ref _ownedBuffers;
    }

    [UnscopedRef]
    private ref NativeMemoryList<long> EntryPositions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Buffers.EntryPositions;
    }

    [UnscopedRef]
    private ref NativeMemoryList<byte> PendingKeys
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
        dest[0] = (byte)BTreeNodeKind.Entry;
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
    /// <see cref="OnEntryAdded"/> so the per-key
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
            dest[0] = (byte)BTreeNodeKind.Entry;
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
            dest[off] = (byte)BTreeNodeKind.Entry;
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
    /// <c>RootPrefix</c> bytes to the root node's <c>BTreeNodeReader.ReadFromStart</c>
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

        // Trigger 3: flush any remaining unflushed entries so BuildIndex can skip its
        // leaf phase entirely. Prune stranded pending first so the final flush only
        // covers entries on the writer's current page; any older entries become direct
        // Entry children of the intermediate level instead.
        //
        // Single-entry HSST short-circuit: when the build holds exactly one entry,
        // bypass FlushPendingNotOnCurrentPage and emit it as a 1-entry inline leaf
        // via forceLeaf:true. Two failure modes are prevented:
        //   1. A page-crossing value would push the lone entry past the writer's
        //      page, FlushPendingNotOnCurrentPage would strand it as a direct Entry
        //      descriptor on CurrentLevel.
        //   2. EmitInlineLeaf's own singleton fast path would route through
        //      FlushPendingAsEntries and also produce a direct Entry descriptor.
        // Either way BuildIndex's currentNative.Count == 1 early-return would
        // mis-report rootSize as the entry record's full byte length
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

        // No data-section reader needed: every descriptor in <c>CurrentLevel</c> carries
        // its first-entry full key in the parallel <c>CurrentLevelFirstKeys</c> list,
        // populated at descriptor-push time (EmitInlineLeaf, FlushPendingAsEntries,
        // FlushPendingNotOnCurrentPage). BuildIndex propagates first-keys as it walks
        // up the tree, so no read-back is required.
        int rootSize = BuildIndex(absoluteIndexStart, maxLeafEntries, maxIntermediateEntries, minLeafEntries, maxIntermediateBytes, minIntermediateChildren, minIntermediateBytes);
        int rootPrefixLen = _rootPrefixLen;

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
        if (rootPrefixLen > 0) CopyRootPrefixBytes(tail[..rootPrefixLen]);
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
    /// for the next add. <paramref name="precomputedLcp"/> is the raw LCP byte count
    /// against <c>Buffers.PrevKeyBuf</c> already computed by
    /// <see cref="MaybeFlushBeforeEntry"/>; pass <c>-1</c> when no precomputed value
    /// is available and the method will walk the prev/current keys itself.
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
    /// <see cref="OnEntryAdded"/> so the per-key
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
        // BTreeNodeWriter: 12-byte header + 4 bytes/entry (u16 prefixArr +
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
    /// <see cref="BTreeNodeKind.Entry"/>-kind descriptor via
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

        WriteIndexNode(children, childFirstKeys, bufs.ValueScratch!, bufs.CommonPrefixArr!, out int leafPrefixLen);

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
    /// <see cref="BTreeNodeKind.Entry"/>-kind descriptor, skipping the leaf
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
    /// page, so push them as <see cref="BTreeNodeKind.Entry"/>-kind
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

    // ─────────── Index-region construction (formerly HsstIndexBuilder) ───────────
    //
    // Builds the B-tree index region. Consumes the per-build state already prepared
    // by the data-region phase above (CurrentLevel / CurrentLevelFirstKeys descriptor
    // lists, CommonPrefixArr) and produces a complete index region where the root
    // index is the last block (readable from end via the trailer).
    //
    // Per-key state during this build phase is one <c>long</c> position. Per-entry
    // common-prefix lengths against the prior entry's key are precomputed online in
    // <see cref="OnEntryAdded"/> into <c>Buffers.CommonPrefixArr</c>; leaf separators
    // are derived as <c>min(commonPrefix + 1, currKeyLen)</c>. Internal-node
    // separators are derived the same way — adjacency of <see cref="HsstIndexNodeInfo"/>
    // ranges means <c>commonPrefixArr[curr.FirstEntry]</c> already holds the LCP
    // between the left-subtree's last key and the right-subtree's first key; the
    // separator bytes are taken from the right-subtree's first key, sourced from the
    // parallel <see cref="HsstBTreeBuilderBuffers.CurrentLevelFirstKeys"/> list. The
    // buffered first-keys avoid reaching back into the already-written data region
    // for a key whose bytes may straddle a 4 KiB page boundary.

    private const int MaxKeyLen = 255;

    // Root's common-key-prefix length, populated by <see cref="BuildIndex"/> for the
    // trailer. Zero for empty HSSTs.
    private int _rootPrefixLen;

    /// <summary>
    /// Build the B-tree index region via <c>_writer</c>. The absolute data-region
    /// start offset (= dataLen) is needed to compute child offsets. Returns the byte
    /// length of the root node — the caller writes the trailer
    /// <c>[RootPrefix bytes][RootPrefixLen u8][RootSize u16][KeyLength u8][IndexType u8]</c>
    /// using that value plus <c>_rootPrefixLen</c> and the bytes obtained from
    /// <see cref="CopyRootPrefixBytes"/> so readers can locate the root from the HSST
    /// end and supply the root's prefix bytes when parsing its header.
    /// </summary>
    private int BuildIndex(long absoluteIndexStart,
        int maxLeafEntries,
        int maxIntermediateEntries,
        int minLeafEntries,
        int maxIntermediateBytes,
        int minIntermediateChildren,
        int minIntermediateBytes)
    {
        long startWritten = _writer.Written;
        long firstOffset = _writer.FirstOffset;

        // Root prefix tracking: the final node emitted is the root.
        _rootPrefixLen = 0;
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        if (bufs.EntryPositions.Count == 0)
        {
            // Empty index: write a single empty index node.
            return WriteEmptyIndexNode();
        }

        if (minIntermediateChildren > maxIntermediateEntries) minIntermediateChildren = maxIntermediateEntries;
        if (minIntermediateChildren < 1) minIntermediateChildren = 1;
        if (minIntermediateBytes < 0) minIntermediateBytes = 0;
        if (minIntermediateBytes > maxIntermediateBytes) minIntermediateBytes = maxIntermediateBytes;

        int valueScratchEntries = Math.Max(maxLeafEntries, maxIntermediateEntries);
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.ValueScratch, Math.Max(64, valueScratchEntries * (2 + 8)));
        byte[] valueScratchArr = bufs.ValueScratch!;
        byte[] commonPrefixArr = bufs.CommonPrefixArr!;

        // CurrentLevel is pre-populated by the inline-leaf emission above (every
        // <c>NaiveLeafBatchSize</c> entries during Add, plus a final trigger 3 flush
        // at Build start). BuildIndex is purely the intermediate-construction loop —
        // no leaf phase, no LeafBoundaryEnumerator, no PrecomputeCommonPrefixLengths.
        // The parallel CurrentLevelFirstKeys list carries each descriptor's
        // first-entry full key in matching order so this loop never re-reads the
        // data section.
        ref NativeMemoryList<HsstIndexNodeInfo> currentNative = ref bufs.CurrentLevel;
        ref NativeMemoryList<HsstIndexNodeInfo> nextNative = ref bufs.NextLevel;
        ref NativeMemoryList<byte> currentFirstKeys = ref bufs.CurrentLevelFirstKeys;
        ref NativeMemoryList<byte> nextFirstKeys = ref bufs.NextLevelFirstKeys;
        nextNative.Clear();
        nextFirstKeys.Clear();

        int lastNodeLen = 0;
        int lastNodePrefixLen = 0;

        // If level 0 has a single node (one page-local leaf written by trigger 3), it
        // IS the root — return its byte length without writing any intermediate. The
        // leaf was just written above, so its bytes occupy
        // <c>[only.ChildOffset, absoluteIndexStart)</c>. The leaf descriptor carries
        // the planner-picked prefix length recorded at EmitInlineLeaf time; that
        // becomes the root's prefix length for the trailer.
        if (currentNative.Count == 1)
        {
            HsstIndexNodeInfo only = currentNative.AsSpan()[0];
            _rootPrefixLen = only.PrefixLen;
            CaptureRootFirstKey(ref bufs, currentFirstKeys.AsSpan());
            return checked((int)(absoluteIndexStart - only.ChildOffset));
        }

        bool firstNode = true;

        // Build internal levels until single root.
        while (currentNative.Count > 1)
        {
            nextNative.Clear();
            nextFirstKeys.Clear();
            ReadOnlySpan<HsstIndexNodeInfo> current = currentNative.AsSpan();
            ReadOnlySpan<byte> currentFirstKeysSpan = currentFirstKeys.AsSpan();
            int childIdx = 0;

            while (childIdx < current.Length)
            {
                int childCount = ChooseIntermediateChildCount(
                    current, currentFirstKeysSpan, childIdx,
                    maxIntermediateEntries, maxIntermediateBytes,
                    minIntermediateChildren, minIntermediateBytes,
                    _writer.Written, firstOffset,
                    commonPrefixArr);
                ReadOnlySpan<HsstIndexNodeInfo> children = current.Slice(childIdx, childCount);
                ReadOnlySpan<byte> childFirstKeys = _keyLength == 0
                    ? default
                    : currentFirstKeysSpan.Slice(childIdx * _keyLength, childCount * _keyLength);

                // First intermediate of the index region: skip the leading pad so we
                // don't insert a hole between the last page-local leaf (data region)
                // and the first intermediate. From the second intermediate onward,
                // pad to a fresh page if we're close to the boundary.
                if (!firstNode) MaybePadToNextPage();
                firstNode = false;

                long nodeStart = _writer.Written;
                long relativeStart = nodeStart - startWritten;
                WriteIndexNode(children, childFirstKeys, valueScratchArr, commonPrefixArr, out int intermediatePrefixLen);
                int nodeLen = checked((int)(_writer.Written - nodeStart));
                lastNodeLen = nodeLen;
                lastNodePrefixLen = intermediatePrefixLen;

                HsstIndexNodeInfo first = children[0];
                HsstIndexNodeInfo last = children[childCount - 1];

                long childOffset = absoluteIndexStart + relativeStart;

                nextNative.Add(new HsstIndexNodeInfo(
                    childOffset,
                    first.FirstEntry,
                    last.LastEntry,
                    intermediatePrefixLen));
                // The intermediate's first-key = its leftmost child's first-key.
                if (_keyLength > 0) nextFirstKeys.AddRange(childFirstKeys[.._keyLength]);

                childIdx += childCount;
            }

            // Swap roles for the next level — ref reassignment, no struct copy.
            ref NativeMemoryList<HsstIndexNodeInfo> tmpNodes = ref currentNative;
            currentNative = ref nextNative;
            nextNative = ref tmpNodes;
            ref NativeMemoryList<byte> tmpKeys = ref currentFirstKeys;
            currentFirstKeys = ref nextFirstKeys;
            nextFirstKeys = ref tmpKeys;
        }

        _rootPrefixLen = lastNodePrefixLen;
        CaptureRootFirstKey(ref bufs, currentFirstKeys.AsSpan());
        return lastNodeLen;
    }

    /// <summary>
    /// Persist the root's first-entry full key into <see cref="HsstBTreeBuilderBuffers.RootFirstKey"/>
    /// so <see cref="CopyRootPrefixBytes"/> can supply the trailer's RootPrefix bytes from
    /// memory rather than re-reading the data section. The ref-local flip of
    /// CurrentLevelFirstKeys / NextLevelFirstKeys in <see cref="BuildIndex"/> means at the
    /// moment this is called, <paramref name="finalLevelKeys"/> is the span of the level
    /// that holds the surviving root descriptor.
    /// </summary>
    private static void CaptureRootFirstKey(scoped ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> finalLevelKeys)
    {
        if (finalLevelKeys.Length == 0) return;
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.RootFirstKey, finalLevelKeys.Length);
        // finalLevelKeys.Length is one descriptor's worth of bytes (the root); copying
        // every byte is correct because RootFirstKey is sized to at least that span.
        finalLevelKeys.CopyTo(bufs.RootFirstKey);
    }

    /// <summary>
    /// Copy the root node's common-key-prefix bytes into <paramref name="dest"/>. Returns
    /// the number of bytes written (equal to <c>_rootPrefixLen</c>). The bytes come from
    /// entry 0's key — the leftmost entry sits under every level's leftmost descendant,
    /// so its first <c>_rootPrefixLen</c> bytes are the root's CommonKeyPrefix. By the
    /// time this is called, <see cref="BuildIndex"/> has cached the root's full first-key in
    /// <see cref="HsstBTreeBuilderBuffers.RootFirstKey"/>, so no data-section re-read is needed.
    /// </summary>
    private int CopyRootPrefixBytes(scoped Span<byte> dest)
    {
        if (_rootPrefixLen == 0) return 0;
        byte[]? rootFirstKey = Buffers.RootFirstKey;
        if (rootFirstKey is null || rootFirstKey.Length < _rootPrefixLen)
            throw new InvalidOperationException("Root first-key cache not populated by BuildIndex.");
        rootFirstKey.AsSpan(0, _rootPrefixLen).CopyTo(dest);
        return _rootPrefixLen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] != b[i]) return i;
        }
        return minLen;
    }

    private int WriteEmptyIndexNode()
    {
        long nodeStart = _writer.Written;
        scoped BTreeNodeWriter<TWriter> indexWriter = new(ref _writer, new BTreeNodeMetadata
        {
            NodeKind = BTreeNodeKind.Intermediate,
            KeyType = 0,
            BaseOffset = 0,
            KeySlotSize = 1,
            // Empty node has no values; ValueSlotSize = 2 is the smallest supported width
            // and the size that gets encoded into the Flags byte. The values section is
            // 0 bytes either way (KeyCount * ValueSize = 0 * 2 = 0).
            ValueSlotSize = 2,
        }, default, default);
        indexWriter.FinalizeNode();
        return checked((int)(_writer.Written - nodeStart));
    }

    /// <summary>
    /// Unified node writer: emit a <see cref="BTreeNodeKind.Intermediate"/> BTreeNode
    /// node covering the given <paramref name="children"/>. Used for both inline page-local
    /// nodes (each child wraps a single entry; pushed from
    /// <see cref="EmitInlineLeaf"/>) and inner nodes (each child is a previously-emitted
    /// node). The per-child separator length is <c>max(natural LCP + 1, children[i].PrefixLen)</c>:
    /// short separators are widened so the parent's slot always carries every byte of the
    /// child's planner-picked CommonKeyPrefix. The planner then picks this node's own
    /// <c>CommonPrefixLen</c> from the shared per-entry LCP array
    /// (<paramref name="commonPrefixArr"/>) capped at <c>minLen</c> over the sepLengths.
    /// The result is returned via <paramref name="nodePrefixLen"/> so the caller can
    /// record it on the descriptor it pushes for the next level up.
    /// </summary>
    private void WriteIndexNode(
        scoped ReadOnlySpan<HsstIndexNodeInfo> children,
        scoped ReadOnlySpan<byte> childFirstKeys,
        scoped Span<byte> valueScratch,
        byte[] commonPrefixArr,
        out int nodePrefixLen)
    {
        int count = children.Length;
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;

        // Per-child separator length: natural LCP-derived length widened to at least
        // the child's own planner-picked prefix so the parent slot can hand the child
        // every byte of its CommonKeyPrefix at descent time. Backed by a pooled buffer
        // so back-to-back Builds reuse the rent.
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.IndexSepLengthsScratch, count);
        Span<int> sepLengths = bufs.IndexSepLengthsScratch.AsSpan(0, count);
        for (int i = 0; i < count; i++)
        {
            int natural = Math.Min(commonPrefixArr[children[i].FirstEntry] + 1, _keyLength);
            sepLengths[i] = Math.Max(natural, children[i].PrefixLen);
        }

        // Shared per-entry LCP array — cp[entry j] is identical at every level by
        // construction, so the chain-min across the children's entry range is the
        // cross-entry LCP the planner needs.
        int crossEntryLcp = ComputeCrossEntryLcp(children, commonPrefixArr);

        BTreeNodeLayoutPlanner.Plan(sepLengths, crossEntryLcp, _keyLength,
            out int prefixLen, out int keyType, out int keySlotSize, out bool keyLittleEndian);

        // BaseOffset + per-entry value-slot width from child offsets.
        long minOff = children[0].ChildOffset;
        long maxOff = minOff;
        for (int i = 1; i < count; i++)
        {
            long off = children[i].ChildOffset;
            if (off < minOff) minOff = off;
            if (off > maxOff) maxOff = off;
        }
        long baseOffset = 0;
        if (count > 1 && minOff > 0 && minOff < maxOff) baseOffset = minOff;
        int valueSlotSize = MinBytesFor(maxOff - baseOffset);

        Span<byte> commonPrefixBuf = stackalloc byte[prefixLen];
        if (prefixLen > 0)
        {
            // Leftmost child's first-key bytes live at the start of childFirstKeys.
            childFirstKeys[..prefixLen].CopyTo(commonPrefixBuf);
        }

        int perEntryKeyBytes = Math.Max(keySlotSize, _keyLength - prefixLen);
        int keyBufSize = count * (2 + Math.Max(1, perEntryKeyBytes));
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.IndexKeyBufScratch, keyBufSize);
        Span<byte> keyBuf = bufs.IndexKeyBufScratch.AsSpan(0, keyBufSize);
        Span<byte> valueScratchSlice = valueScratch[..(count * (2 + valueSlotSize))];

        scoped BTreeNodeWriter<TWriter> indexWriter = new(ref _writer, new BTreeNodeMetadata
        {
            NodeKind = BTreeNodeKind.Intermediate,
            KeyType = keyType,
            BaseOffset = (ulong)baseOffset,
            KeySlotSize = keySlotSize,
            ValueSlotSize = valueSlotSize,
            IsKeyLittleEndian = keyLittleEndian,
        }, keyBuf, valueScratchSlice, commonPrefixBuf);

        Span<byte> valueBuf = stackalloc byte[8];

        for (int i = 0; i < count; i++)
        {
            // Each child's first-key occupies _keyLength bytes at slot i of childFirstKeys.
            ReadOnlySpan<byte> currKey = _keyLength == 0
                ? default
                : childFirstKeys.Slice(i * _keyLength, _keyLength);
            WriteUInt64LE(valueBuf, children[i].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(
                currKey.Slice(prefixLen, KeySliceLength(prefixLen, keyType, keySlotSize, sepLengths[i])),
                valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
        nodePrefixLen = prefixLen;
    }

    /// <summary>
    /// Compute the chain-min of <c>commonPrefixArr</c> over the entry range covered by
    /// <paramref name="children"/>. Treats <c>commonPrefixArr[entry 0]</c> as the
    /// boundary against the (nonexistent) prior subtree, which is conventionally 0.
    /// </summary>
    private static int ComputeCrossEntryLcp(scoped ReadOnlySpan<HsstIndexNodeInfo> children, byte[] commonPrefixArr)
    {
        if (children.Length == 0) return MaxKeyLen;
        int rangeStart = children[0].FirstEntry;
        int rangeEnd = children[children.Length - 1].LastEntry;
        int chainLcp = MaxKeyLen;
        for (int j = rangeStart + 1; j <= rangeEnd; j++)
        {
            byte v = commonPrefixArr[j];
            if (v < chainLcp) chainLcp = v;
        }
        return chainLcp;
    }

    /// <summary>
    /// Slice the per-entry key bytes for the writer based on layout:
    /// Uniform (keyType=1) takes a fixed <paramref name="keySlotSize"/> bytes;
    /// Variable (keyType=0) takes the entry's natural sep length
    /// (<paramref name="sepLength"/>), prefix-stripped. Both are sliced from
    /// the entry's key starting at <paramref name="prefixLen"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int KeySliceLength(int prefixLen, int keyType, int keySlotSize, int sepLength) =>
        keyType == 1 ? keySlotSize : sepLength - prefixLen;

    /// <summary>
    /// Pick the number of children to pack into the next intermediate node by
    /// summing values + keys section bytes until the next child would push the
    /// estimate over <paramref name="byteThreshold"/> (capped at
    /// <paramref name="maxChildren"/>; always includes at least one child).
    /// </summary>
    private int ChooseIntermediateChildCount(
        scoped ReadOnlySpan<HsstIndexNodeInfo> level,
        scoped ReadOnlySpan<byte> levelFirstKeys,
        int childIdx,
        int maxChildren, int byteThreshold,
        int minChildren, int minBytes,
        long nodeStart, long firstOffset,
        byte[] commonPrefixArr)
    {
        int remaining = level.Length - childIdx;
        int hardMax = Math.Min(maxChildren, remaining);
        if (hardMax <= 1) return hardMax;

        // Slot 0 carries a separator just like every other slot: the natural
        // LCP-derived length widened to at least the child's own planner-picked
        // prefix (WriteIndexNode applies max(natural, PrefixLen) to every slot,
        // index 0 included). Seed maxSepLen / commonLen / firstSep from that same
        // length so the heuristic models what the writer emits — for a non-first
        // group the boundary LCP can exceed firstChild.PrefixLen.
        HsstIndexNodeInfo firstChild = level[childIdx];
        int firstNaturalSep = Math.Min(commonPrefixArr[firstChild.FirstEntry] + 1, _keyLength);
        int firstSepLen = Math.Max(firstNaturalSep, firstChild.PrefixLen);
        int childCount = 1;
        // Max separator length seen so far. Drives both the split heuristic (forcing a
        // split when the next child would widen the planner's Uniform key slot) and the
        // keys-section size estimate — the planner widens every slot to a {2,4,8} width.
        int maxSepLen = firstSepLen;
        // BaseOffset is fixed at the leftmost child's absolute offset; remaining
        // children encode as deltas. valueSlotSize tracks the min byte width for
        // the current max delta over children[0..]; slot 0 itself contributes a 0 delta.
        long baseChildOffset = firstChild.ChildOffset;
        long maxOff = baseChildOffset;
        int committedValueSlot = MinBytesFor(0);
        // Common-prefix length across separators observed so far. With phantom slot 0
        // restored the first separator (firstChild) seeds commonLen and firstSep so the
        // running LCP is meaningful from childCount == 1 onward. firstSep / sepBuf live
        // on the pooled buffers struct so back-to-back Builds reuse the rent instead of
        // re-stackallocating 510 bytes per ChooseIntermediateChildCount call.
        int commonLen = firstSepLen;
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.IndexFirstSepScratch, MaxKeyLen);
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.IndexSepBufScratch, MaxKeyLen);
        Span<byte> firstSep = bufs.IndexFirstSepScratch.AsSpan(0, MaxKeyLen);
        Span<byte> sepBuf = bufs.IndexSepBufScratch.AsSpan(0, MaxKeyLen);
        if (firstSepLen > 0)
        {
            // First child's first-key sits at slot childIdx of levelFirstKeys.
            levelFirstKeys.Slice(childIdx * _keyLength, firstSepLen).CopyTo(firstSep);
        }

        while (childCount < hardMax)
        {
            HsstIndexNodeInfo curr = level[childIdx + childCount];
            // Adjacency invariant: prev.LastEntry == curr.FirstEntry - 1, so
            // commonPrefixArr[curr.FirstEntry] is exactly LCP(leftKey, rightKey).
            // Natural separator length is min(LCP + 1, _keyLength); the actual stored
            // length is widened to at least curr.PrefixLen so the parent's separator
            // carries every byte of the child's prefix at descent time.
            int naturalSep = Math.Min(commonPrefixArr[curr.FirstEntry] + 1, _keyLength);
            int sepLen = Math.Max(naturalSep, curr.PrefixLen);
            // curr's first-key sits at slot (childIdx + childCount) of levelFirstKeys —
            // childCount currently being the number of children already committed in
            // this group, so the next candidate sits exactly after them.
            if (sepLen > 0)
            {
                int rightSlot = (childIdx + childCount) * _keyLength;
                levelFirstKeys.Slice(rightSlot, sepLen).CopyTo(sepBuf);
            }

            long newMaxOff = curr.ChildOffset > maxOff ? curr.ChildOffset : maxOff;
            int valueSlotSize = MinBytesFor(newMaxOff - baseChildOffset);
            int newMaxSepLen = sepLen > maxSepLen ? sepLen : maxSepLen;

            int boundary = Math.Min(commonLen, sepLen);
            int newCommonLen = commonLen == 0
                ? 0
                : CommonPrefixLength(firstSep[..boundary], sepBuf[..boundary]);

            int newCount = childCount + 1;
            // Keys-section size as the writer emits it: a Uniform node packs newCount
            // fixed-width slots, each widened to the planner's {2,4,8} SIMD slot.
            int newKeysBytes = newCount * BTreeNodeLayoutPlanner.WidenedSlotWidth(newMaxSepLen, _keyLength);
            // Phantom slot 0 restored: keys array carries newCount real separators
            // (one per child) and values array carries newCount deltas.
            int estimated = newCount * valueSlotSize + newKeysBytes;
            if (estimated > byteThreshold) break;

            // Dynamic split heuristics. Once minChildren is reached, break only
            // when:
            //   - effective separator (post-LCP-strip) would exceed 8 bytes — past
            //     that the planner can no longer snap to a SIMD-eligible {2,4,8}
            //     Uniform slot. Combines the old "max sep widened" and "LCP shrank"
            //     checks into a single post-strip-width budget; value-slot widening
            //     is allowed.
            //   - WouldCrossNewPage: candidate node would straddle a 4 KiB page
            //     boundary the committed node does not.
            //
            // The effective separator looks ahead two children — `curr` plus the
            // entry after it — rather than just `curr`. When that following entry
            // carries a high separator, breaking before `curr` makes it an
            // internal (non-first) child of the next node, so the high separator
            // stays at this level instead of surfacing one level up as the next
            // node's parent-level separator.
            int effMaxSepLen = newMaxSepLen;
            int effCommonLen = newCommonLen;
            int next2Idx = childIdx + childCount + 1;
            if (next2Idx < level.Length)
            {
                HsstIndexNodeInfo next2 = level[next2Idx];
                int next2NaturalSep = Math.Min(commonPrefixArr[next2.FirstEntry] + 1, _keyLength);
                int next2SepLen = Math.Max(next2NaturalSep, next2.PrefixLen);
                if (next2SepLen > effMaxSepLen) effMaxSepLen = next2SepLen;

                // Chain the running group prefix against next2's separator bytes,
                // capped at min(newCommonLen, next2SepLen). sepBuf currently holds
                // curr's bytes — already consumed by the newCommonLen computation
                // above — so overwriting it with next2's bytes here is safe.
                int next2Boundary = Math.Min(effCommonLen, next2SepLen);
                if (next2Boundary > 0)
                    levelFirstKeys.Slice(next2Idx * _keyLength, next2Boundary).CopyTo(sepBuf);
                effCommonLen = effCommonLen == 0
                    ? 0
                    : CommonPrefixLength(firstSep[..next2Boundary], sepBuf[..next2Boundary]);
            }
            int newEffSepLen = effMaxSepLen - effCommonLen;
            int candidateSize = IntermediateNodeSizeUpperBound(newCount, newKeysBytes, valueSlotSize);
            int committedSize = IntermediateNodeSizeUpperBound(
                childCount,
                childCount * BTreeNodeLayoutPlanner.WidenedSlotWidth(maxSepLen, _keyLength),
                committedValueSlot);
            if (childCount >= minChildren &&
                committedSize >= minBytes &&
                (newEffSepLen > 8 ||
                 WouldCrossNewPage(nodeStart, firstOffset, committedSize, candidateSize)))
                break;

            childCount = newCount;
            maxOff = newMaxOff;
            committedValueSlot = valueSlotSize;
            maxSepLen = newMaxSepLen;
            commonLen = newCommonLen;
        }
        return childCount;
    }

    // Conservative upper bound on BTreeNodeWriter header bytes: 12 base
    // (Flags + KeyCount u16 + KeySize u16 + ValueSize u8 + BaseOffset 6) + 1
    // optional CommonPrefixLen byte + a small slack.
    private const int NodeHeaderUpperBound = 16;

    // Conservative upper bound on an intermediate node's serialised size with phantom
    // slot 0 restored: a node holding <paramref name="count"/> children emits a
    // <paramref name="keysSectionBytes"/>-byte keys section and <paramref name="count"/>
    // values. The per-entry term (2 + valueSlotSize) intentionally over-allocates by 2
    // bytes per value: Uniform values on disk are just valueSlotSize bytes each (no
    // length prefix), but the +2 absorbs Variable-section length-table overhead and
    // rounding slack so the bound stays above the actual size for every layout the
    // planner picks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IntermediateNodeSizeUpperBound(int count, int keysSectionBytes, int valueSlotSize)
        => NodeHeaderUpperBound + keysSectionBytes + count * (2 + valueSlotSize);

    /// <summary>
    /// True if a node of <paramref name="candidateSize"/> bytes starting at
    /// <paramref name="nodeStart"/> would straddle a 4 KiB page boundary that the
    /// already-committed node of <paramref name="committedSize"/> bytes does not.
    /// Pages are aligned relative to <paramref name="firstOffset"/>, matching the
    /// writer's <see cref="IByteBufferWriter.FirstOffset"/> contract.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool WouldCrossNewPage(long nodeStart, long firstOffset, int committedSize, int candidateSize)
    {
        long pageOff = (nodeStart - firstOffset) & PageLayout.PageMask;
        bool committedCrosses = pageOff + committedSize > PageLayout.PageSize;
        bool candidateCrosses = pageOff + candidateSize > PageLayout.PageSize;
        return candidateCrosses && !committedCrosses;
    }

    /// <summary>
    /// If the writer is within <see cref="PageLayout.PadThreshold"/> bytes of the
    /// next 4 KiB boundary, pad up to that boundary so the next node starts on a
    /// fresh page. Companion to <see cref="WouldCrossNewPage"/>: the page-crossing
    /// heuristic stops a node growing into the next page, but the next node would
    /// then start at the seam and be guaranteed to cross. Padding bytes are inert:
    /// parent nodes record exact child offsets, so readers never look at the
    /// padding region. Caller must avoid invoking this after the very last node
    /// (root) — the trailer formula <c>root_start = HSST_end - 4 - rootSize</c>
    /// assumes the trailer abuts the root, and any padding between them would
    /// offset the computed root start.
    /// </summary>
    private void MaybePadToNextPage()
    {
        long firstOffset = _writer.FirstOffset;
        long pageOff = (_writer.Written - firstOffset) & PageLayout.PageMask;
        if (pageOff == 0) return;
        long remaining = PageLayout.PageSize - pageOff;
        if (remaining > PageLayout.PadThreshold) return;
        int len = (int)remaining;
        Span<byte> pad = _writer.GetSpan(len);
        pad[..len].Clear();
        _writer.Advance(len);
    }

    /// <summary>
    /// Forwarding shim — see <see cref="HsstValueSlot.MinBytesFor"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinBytesFor(long value) => HsstValueSlot.MinBytesFor(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64LE(Span<byte> dest, long value, int width)
    {
        for (int i = 0; i < width; i++)
            dest[i] = (byte)(value >> (i * 8));
    }
}
