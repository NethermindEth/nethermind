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
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries, which MUST be
/// added in sorted key order (no internal sorting). The <c>keyFirst</c> constructor flag
/// selects the data-region entry layout: <c>false</c> is key-after-value and supports the
/// streaming <see cref="BeginValueWrite"/> / <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long)"/>
/// API; <c>true</c> is key-first and requires <see cref="Add(System.ReadOnlySpan{byte},System.ReadOnlySpan{byte})"/>.
/// </summary>
/// <remarks>
/// Wire layout: see <c>Hsst/FORMAT.md</c>, "BTree variant" (<c>keyFirst = false</c>) and
/// "BTreeKeyFirst variant" (<c>keyFirst = true</c>).
/// <para>
/// Memory: while the data section is being written, the only per-key state held in
/// memory is one <c>long</c> per entry (the entry's index pointer target — MetadataStart
/// in key-after-value mode, EntryStart in key-first mode). Separators and the previous
/// key are not buffered — at <see cref="Build"/> time the index builder is handed a
/// reader over the just-written data section and recomputes separators on-demand from
/// the flushed bytes.
/// </para>
/// </remarks>
public ref partial struct HsstBTreeBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private long _writtenBeforeValue;
    private readonly long _baseOffset;
    private readonly bool _keyFirst;
    private int _keyLength;

    // Root's common-key-prefix length, populated by BuildIndex (see HsstBTreeBuilder.Index.cs)
    // for the trailer. Zero for empty HSSTs. Declared here so all instance fields live in one
    // partial declaration (CS0282).
    private int _rootPrefixLen;

    // Ref to the caller's HsstBTreeBuilderBuffers. The caller owns and disposes the
    // buffer; the builder holds a borrowed ref for the duration of the build.
    // HsstBTreeBuilder is a ref struct so a ref field is allowed; HsstBTreeBuilderBuffers
    // is not a ref struct so CS9050 doesn't apply.
    private readonly ref HsstBTreeBuilderBuffers _buffers;

    // Global, build-wide entry count — incremented once per Add / FinishValueWrite.
    // Doubles as the next entry's index, the upper bound of CommonPrefixArr's valid
    // range, and the global FirstEntry / LastEntry value stamped on each per-entry
    // <see cref="HsstIndexNodeInfo"/> descriptor.
    private int _entryCount;

    // Count of trailing descriptors in <c>Buffers.CurrentLevel</c> that are still
    // Entry-kind candidates for a page-local leaf wrap. Each Add pushes one Entry
    // descriptor onto CurrentLevel and increments this counter;
    // <see cref="MaybeEmitInlineLeaf"/> pops the trailing on-page run and replaces it
    // with a single leaf descriptor; <see cref="FlushPendingAsEntries"/> and
    // <see cref="FlushPendingNotOnCurrentPage"/> simply drop entries from the
    // pending count (the descriptors stay in place, now sealed as direct Entry
    // children of whatever intermediate the index-build phase puts above them).
    private int _pendingCount;

    // Set the first time <see cref="MaybeEmitInlineLeaf"/> actually writes a leaf node
    // (and stays set for the rest of the build). Lets <see cref="Build"/>'s
    // single-entry-HSST post-process distinguish a lone Entry descriptor (no leaf
    // ever wrapped — needs wrapping to keep rootSize in the u16 trailer) from a
    // lone Leaf descriptor (already bounded, no action).
    private bool _hasEmittedLeaf;

    // Writer's page index (writer.Written / PageLayout.PageSize) at the last
    // observation point. Used by MaybeFlushBeforeEntry to gate the
    // FlushPendingNotOnCurrentPage call — entries can only become stranded on a
    // prior page when the writer's own page index has advanced, and Add() is the
    // only path that mutates the writer between consecutive Adds, so the gate is
    // safe.
    private long _lastWriterPage;

    /// <summary>
    /// Create a builder that writes via <paramref name="writer"/> and uses
    /// <paramref name="buffers"/> as its working storage. The caller owns the
    /// buffer's lifetime — allocate one (typically via
    /// <c>using HsstBTreeBuilderBuffersContainer buffers = new(expectedKeyCount);</c>,
    /// then pass <c>ref buffers.Buffers</c>) and dispose it after the build.
    /// </summary>
    /// <remarks>
    /// The trailing [RootSize u16][KeyLength u8][IndexType u8] is appended in <see cref="Build"/>.
    /// <para>
    /// <paramref name="buffers"/> is reset for this build via
    /// <see cref="HsstBTreeBuilderBuffers.ResetForBuild"/>, so the same buffer can be
    /// passed to back-to-back builds — the entry-positions list, common-prefix array,
    /// leaf-first-keys, level lists, value scratch, segment tree, and DFS stack stay
    /// rented across invocations.
    /// </para>
    /// <para>
    /// <paramref name="keyLength"/> declares the fixed key length (0–255) every entry must use;
    /// all keys in a single HSST must be exactly this many bytes. Pass -1 to defer the
    /// declaration to the first <see cref="Add"/>/<see cref="FinishValueWrite"/>
    /// call, which then locks the length for the rest of the build. The fixed length is
    /// recorded once in the trailer (single KeyLength:u8 byte before the IndexType byte)
    /// rather than per-entry, and the builder rejects mismatches at build time so readers
    /// can rely on the trailer value.
    /// </para>
    /// <para>
    /// <paramref name="expectedKeyCount"/> sizes the entry-positions buffer up front;
    /// pass an estimate when known to avoid resize allocations. The buffer still grows on demand.
    /// </para>
    /// <para>
    /// When <paramref name="keyFirst"/> is true, the data-region entries are written
    /// key-first (<c>[FullKey][LEB128][Value]</c>) and the trailer carries
    /// <see cref="IndexType.BTreeKeyFirst"/>; <see cref="BeginValueWrite"/> is rejected
    /// because the value length must be known up front, so callers must use
    /// <see cref="Add"/>.
    /// </para>
    /// </remarks>
    public HsstBTreeBuilder(ref TWriter writer, ref HsstBTreeBuilderBuffers buffers, int keyLength, int expectedKeyCount = 16, bool keyFirst = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keyLength, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyLength, 255);

        _writer = ref writer;
        _baseOffset = _writer.Written;
        _keyLength = keyLength;
        _keyFirst = keyFirst;

        buffers.ResetForBuild(expectedKeyCount);
        _buffers = ref buffers;
        _entryCount = 0;
        _pendingCount = 0;
        _hasEmittedLeaf = false;
        _lastWriterPage = (_writer.Written - _writer.FirstOffset) / PageLayout.PageSize;
        PrimePerAddBuffers(ref buffers, expectedKeyCount, keyLength);
    }

    /// <summary>Pre-grow CommonPrefixArr and (when keyLength is known) PrevKeyBuf capacity so the per-Add hot path avoids regrows.</summary>
    private static void PrimePerAddBuffers(ref HsstBTreeBuilderBuffers buffers, int expectedKeyCount, int keyLength)
    {
        int cpCap = Math.Max(expectedKeyCount, 64);
        buffers.CommonPrefixArr.EnsureCapacity(cpCap);
        if (keyLength > 0)
            buffers.PrevKeyBuf.EnsureCapacity(keyLength);
    }

    /// <summary>
    /// No-op: the caller owns and disposes the <see cref="HsstBTreeBuilderBuffers"/>
    /// passed to the constructor. Kept so existing <c>using HsstBTreeBuilder&lt;…&gt;</c>
    /// call sites compile unchanged.
    /// </summary>
    public void Dispose() { }

    /// <summary>Reference to the caller-owned <see cref="HsstBTreeBuilderBuffers"/>.</summary>
    [UnscopedRef]
    private ref HsstBTreeBuilderBuffers Buffers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _buffers;
    }

    /// <summary>
    /// Begin writing a value. Returns ref to the shared writer and snapshots Written.
    /// Close the entry with <see cref="FinishValueWrite(ReadOnlySpan{byte}, long)"/>, which
    /// documents the leading-padding / page-alignment handling.
    ///
    /// Not supported in key-first mode (the value length must be known when the entry
    /// is laid down). Callers in key-first mode must use <see cref="Add"/>.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        if (_keyFirst)
            throw new InvalidOperationException("Key-first BTree requires Add(key, value); BeginValueWrite/FinishValueWrite streaming is not supported.");
        // Trigger 1: a streaming value is about to flow and will straddle pages, so seal any
        // pending leaf now to keep it colocated with its entries.
        MaybeEmitInlineLeaf();
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish value write with an explicit value length. <paramref name="valueLength"/>
    /// is the number of bytes the caller wrote into the writer between the matching
    /// <see cref="BeginValueWrite"/> snapshot and now that should be treated as the
    /// value. The writer may have been advanced past <paramref name="valueLength"/>
    /// bytes — any leading bytes between the snapshot and
    /// <c>(Written − valueLength)</c> are treated as padding and become inert gap
    /// data that no index entry points at. Use this to keep a value from crossing a
    /// 4 KiB page boundary by padding ahead of it.
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
        // value/LEB128 that follow.
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

        // No precomputed LCP available on this path — EmitEntryBookkeeping will compute
        // it from PrevKeyBuf. AddCore forwards its own MaybeFlushBeforeEntry-derived LCP
        // through EmitEntryBookkeeping directly, without routing through this method.
        EmitEntryBookkeeping(ref Buffers, key, metadataPos, precomputedLcp: -1);
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
        // Best-effort page alignment; the entry lands unaligned when it can't be padded.
        TryAlign(entryLen);
        AddCore(ref bufs, key, value, lebSize, lcp);
    }

    /// <summary>Pad to the next page when the entry would straddle a boundary, up to <see cref="PageLayout.PadThreshold"/>. Returns false when the entry exceeds one page or the pad would exceed the threshold.</summary>
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
    /// so it does not pay double page-math. <paramref name="precomputedLcp"/> is
    /// the raw LCP byte count returned by <see cref="MaybeFlushBeforeEntry"/>
    /// (<c>-1</c> if unknown) and is forwarded into
    /// <see cref="EmitEntryBookkeeping"/> so the per-key
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
    /// Per-entry bookkeeping shared by the buffered <see cref="AddCore"/> path and the
    /// streaming <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long)"/> path: push the
    /// entry's index pointer (MetadataStart in key-after-value mode, EntryStart in key-first
    /// mode) and first-key onto the level-0 lists, then record the LCP / PendingMaxSepLen and
    /// refresh PrevKeyBuf. <paramref name="precomputedLcp"/> is the LCP against
    /// <c>PrevKeyBuf</c> when the caller already has it (AddCore forwards the value from
    /// <see cref="MaybeFlushBeforeEntry"/>); <c>-1</c> recomputes it from prev/current keys.
    /// <paramref name="bufs"/> is the same ref the caller already resolved, threaded through to
    /// avoid re-resolving the <see cref="Buffers"/> branch on every Add.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitEntryBookkeeping(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, long entryPos, int precomputedLcp)
    {
        // Push the per-entry descriptor and its first-key directly onto the level-0
        // lists. FirstEntry == LastEntry == entryIdx tags the descriptor with its
        // global entry index — used by WriteIndexNode / ChooseIntermediateChildCount
        // to look up CommonPrefixArr[FirstEntry] when this descriptor (or its
        // enclosing leaf) becomes a child of an intermediate node.
        int entryIdx = _entryCount;
        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(entryPos, entryIdx, entryIdx, prefixLen: 0));
        if (key.Length > 0) bufs.CurrentLevelFirstKeys.AddRange(key);
        _pendingCount++;
        _entryCount++;

        // Record this entry's LCP against the previous entry's key in CommonPrefixArr
        // (appended in order — Count == entryIdx before this Add).
        int cp = 0;
        if (entryIdx > 0 && _keyLength > 0)
        {
            cp = precomputedLcp >= 0
                ? precomputedLcp
                : MemoryExtensions.CommonPrefixLength(bufs.PrevKeyBuf.AsSpan(), key);
        }
        bufs.CommonPrefixArr.Add((byte)cp);

        // Incremental update of PendingMaxSepLen so MaybeFlushBeforeEntry can skip its
        // O(pending) scan: sepLen for an entry is min(cp + 1, keyLength), and we want the max
        // over the pending range (rebuilt by FlushPendingNotOnCurrentPage's partial-flush rescan).
        if (_keyLength > 0)
        {
            byte sl = (byte)Math.Min(cp + 1, _keyLength);
            if (sl > bufs.PendingMaxSepLen) bufs.PendingMaxSepLen = sl;
        }

        // Refresh PrevKeyBuf for the next entry's LCP: hold exactly this entry's key.
        if (_keyLength > 0 && key.Length == _keyLength)
        {
            bufs.PrevKeyBuf.Clear();
            bufs.PrevKeyBuf.AddRange(key);
        }
    }

    /// <summary>Builds the index region and appends the trailer.</summary>
    /// <remarks>
    /// Trailer layout and root-location arithmetic: see <c>Hsst/FORMAT.md</c>, "BTree variant".
    /// <c>RootPrefix</c> carries the root's common-key-prefix bytes (the root has no parent
    /// separator to inherit them from). <c>KeyLength</c> is 0 when the build was empty.
    /// </remarks>
    public unsafe void Build()
    {
        // Trigger 3: flush any remaining unflushed entries so BuildIndex can skip its
        // leaf phase entirely.
        MaybeEmitInlineLeaf();

        // Single-entry-HSST post-process: if the build holds exactly one entry and
        // no leaf was ever written (e.g. the lone entry's value crossed pages, so
        // the on-page filter dropped it from the pending count), the lone
        // CurrentLevel descriptor is a direct Entry — BuildIndex's
        // currentNative.Count == 1 early-return would mis-report rootSize as the
        // entry record's full byte length (1 + keyLen + LEB128 + valueLen), which
        // overflows the u16 trailer for large values. Wrap it in a 1-entry leaf so
        // the root is a bounded node.
        if (_entryCount == 1 && !_hasEmittedLeaf) WrapLoneEntryAsLeaf();

        long dataSectionSize = _writer.Written - _baseOffset;
        long absoluteIndexStart = dataSectionSize;

        // No data-section reader needed: every descriptor in <c>CurrentLevel</c> carries
        // its first-entry full key in the parallel <c>CurrentLevelFirstKeys</c> list,
        // populated at descriptor-push time (MaybeEmitInlineLeaf, FlushPendingAsEntries,
        // FlushPendingNotOnCurrentPage). BuildIndex propagates first-keys as it walks
        // up the tree, so no read-back is required.
        int rootSize = BuildIndex(absoluteIndexStart);
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
    /// Trigger 2 (page-boundary fit): flush the pending set as a leaf when the next entry plus that leaf would
    /// straddle the current 4 KiB page. Returns the raw LCP between <paramref name="key"/> and PrevKeyBuf
    /// (<c>-1</c> when no meaningful LCP exists) so the caller can thread it into EmitEntryBookkeeping.
    /// </summary>
    private int MaybeFlushBeforeEntry(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, long entryLen)
    {
        // Compute LCP once at the top; reused for the leaf-fit estimate below and
        // returned for the caller to forward into EmitEntryBookkeeping. Uses PrevKeyBuf
        // (set by the last EmitEntryBookkeeping) — survives flushes that clear the pending
        // range, and stays valid even when the prior entry was stranded onto the
        // previous page and sealed as a direct Entry descriptor.
        int lcp = -1;
        if (_keyLength > 0 && key.Length == _keyLength && bufs.PrevKeyBuf.Count >= _keyLength)
        {
            lcp = MemoryExtensions.CommonPrefixLength(bufs.PrevKeyBuf.AsSpan(), key);
        }

        int pending = _pendingCount;
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
            pending = _pendingCount;
            if (pending < 1) return lcp;
        }

        int newSepLen = lcp >= 0 ? Math.Min(lcp + 1, _keyLength) : _keyLength;

        // Max sep length over pending entries is maintained incrementally by
        // EmitEntryBookkeeping (and rebuilt by FlushPendingNotOnCurrentPage's
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
        // a singleton goes out as a direct Entry descriptor via MaybeEmitInlineLeaf's
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
        {
            // Seal the trailing pending run in place: each pending descriptor is already an
            // Entry-kind descriptor in CurrentLevel, so dropping the pending count makes the
            // future intermediate node point at the entries directly (no cross-page leaf).
            _pendingCount = 0;
            Buffers.PendingMaxSepLen = 0;
        }
        else
            MaybeEmitInlineLeaf();

        return lcp;
    }

    private const int PageLocalLeafHeaderBytes = 12;
    private const int PageLocalLeafValueSlotBytes = 2;

    /// <summary>
    /// Write a page-local leaf node into the data region for the trailing pending run
    /// of Entry descriptors in <c>Buffers.CurrentLevel</c>, then pop those descriptors
    /// and push the leaf descriptor in their place. Clears <see cref="_pendingCount"/>.
    /// No-op when nothing is pending.
    /// </summary>
    /// <remarks>
    /// On-page filter: the pending run can span multiple writer pages if a streaming
    /// value (<see cref="BeginValueWrite"/>) or a large Add advanced the writer past
    /// a 4 KiB boundary while entries were still accumulating. The leaf wrap covers
    /// only the contiguous on-current-page suffix — earlier pending descriptors stay
    /// in <c>CurrentLevel</c> as sealed direct Entry children (no data movement,
    /// just a counter drop) so the intermediate node above can point at them through
    /// the reader's uniform flag-byte dispatch.
    ///
    /// Singleton fast path: when the on-page pending run is exactly one descriptor,
    /// the leaf wrap is pure overhead (12-byte header + per-entry slot + tail key
    /// bytes) — the lone Entry descriptor is already on <c>CurrentLevel</c>, so just
    /// clear the pending counter. The single-entry-HSST corner case (where the lone
    /// descriptor would otherwise become the root, and BuildIndex's
    /// <c>currentNative.Count == 1</c> early-return would mis-report its unbounded
    /// record length as rootSize) is handled separately in <see cref="Build"/>'s
    /// post-process — see <see cref="WrapLoneEntryAsLeaf"/>.
    /// </remarks>
    private void MaybeEmitInlineLeaf()
    {
        if (_pendingCount == 0) return;

        // On-page filter: drop off-page pending entries from the count. They stay
        // in CurrentLevel as sealed Entry descriptors — same shape they would have
        // had under the legacy FlushPendingNotOnCurrentPage → push path. Also
        // refreshes _lastWriterPage so the next per-Add gate check is a single cmp.
        FlushPendingNotOnCurrentPage();
        if (_pendingCount == 0) return;

        // Singleton short-circuit: the lone Entry descriptor is already on
        // CurrentLevel with its first-key in CurrentLevelFirstKeys; just seal.
        if (_pendingCount == 1)
        {
            ref HsstBTreeBuilderBuffers bufsSingleton = ref Buffers;
            _pendingCount = 0;
            bufsSingleton.PendingMaxSepLen = 0;
            return;
        }

        long nodeStart = _writer.Written - _baseOffset;

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        int count = _pendingCount;

        // The pending Entry descriptors are the trailing <c>count</c> slots of
        // CurrentLevel; their first-keys are the trailing <c>count * _keyLength</c>
        // bytes of CurrentLevelFirstKeys. Pass slices straight into WriteIndexNode —
        // no per-entry stackalloc, no read-back from a shadow buffer.
        Span<HsstIndexNodeInfo> currentLevelSpan = bufs.CurrentLevel.AsSpan();
        int childrenStart = currentLevelSpan.Length - count;
        ReadOnlySpan<HsstIndexNodeInfo> children = currentLevelSpan.Slice(childrenStart, count);
        Span<byte> firstKeysSpan = bufs.CurrentLevelFirstKeys.AsSpan();
        int keysStart = firstKeysSpan.Length - count * _keyLength;
        ReadOnlySpan<byte> childFirstKeys = _keyLength == 0
            ? default
            : firstKeysSpan.Slice(keysStart, count * _keyLength);

        int firstEntryIdx = children[0].FirstEntry;
        int lastEntryIdx = children[count - 1].LastEntry;

        WriteIndexNode(children, childFirstKeys, bufs.CommonPrefixArr.AsSpan(), out int leafPrefixLen);

        // Pop the per-entry descriptors; push the leaf descriptor. CurrentLevelFirstKeys
        // keeps the leftmost popped entry's key in place at offset <c>keysStart</c> —
        // that block is the leaf's first-key, so a single Truncate to
        // <c>(currentLevelSpan.Length - count + 1) * _keyLength</c> drops only the
        // (count - 1) following key blocks; no copy needed.
        bufs.CurrentLevel.Truncate(childrenStart);
        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(nodeStart, firstEntryIdx, lastEntryIdx, leafPrefixLen));
        if (_keyLength > 0) bufs.CurrentLevelFirstKeys.Truncate(keysStart + _keyLength);

        _pendingCount = 0;
        _hasEmittedLeaf = true;
        bufs.PendingMaxSepLen = 0;
    }

    /// <summary>
    /// Post-process called by <see cref="Build"/> for the single-entry HSST case
    /// when no leaf has been emitted. Wraps the lone direct Entry descriptor sitting
    /// in <c>CurrentLevel</c> as a 1-entry leaf node so the root is a bounded node
    /// and <see cref="BuildIndex"/>'s single-root early-return reports a u16-fittable
    /// rootSize. Unlike <see cref="MaybeEmitInlineLeaf"/>, this bypasses the on-page
    /// filter — a cross-page leaf is acceptable here because the alternative (a
    /// direct Entry root) would overflow the u16 trailer for any value past ~64 KiB.
    /// </summary>
    private void WrapLoneEntryAsLeaf()
    {
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        Debug.Assert(bufs.CurrentLevel.Count == 1, "WrapLoneEntryAsLeaf expects a single descriptor on CurrentLevel.");
        Debug.Assert(_entryCount == 1, "WrapLoneEntryAsLeaf is only valid for single-entry builds.");

        long nodeStart = _writer.Written - _baseOffset;
        ReadOnlySpan<HsstIndexNodeInfo> children = bufs.CurrentLevel.AsSpan();
        ReadOnlySpan<byte> childFirstKeys = _keyLength == 0
            ? default
            : bufs.CurrentLevelFirstKeys.AsSpan()[.._keyLength];

        int firstEntryIdx = children[0].FirstEntry;
        int lastEntryIdx = children[0].LastEntry;

        WriteIndexNode(children, childFirstKeys, bufs.CommonPrefixArr.AsSpan(), out int leafPrefixLen);

        // Replace the lone Entry descriptor with the leaf descriptor. The lone
        // first-key block in CurrentLevelFirstKeys is also the leaf's first-key,
        // so it stays untouched.
        bufs.CurrentLevel.Truncate(0);
        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(nodeStart, firstEntryIdx, lastEntryIdx, leafPrefixLen));
        _hasEmittedLeaf = true;
    }

    /// <summary>
    /// Trim the trailing pending run in <c>CurrentLevel</c> to only the descriptors
    /// whose flag byte (= the key region) sits on the writer's current page. Older
    /// pending descriptors are stranded on prior pages and can't share a page-local
    /// leaf with anything on the writer's current page; they become sealed direct
    /// Entry children of the intermediate above (no data movement — they're already
    /// the right shape, just no longer counted as pending). Also refreshes
    /// <see cref="_lastWriterPage"/> for the next per-Add gate check.
    ///
    /// Entries are written with monotonically increasing positions, so the stranded
    /// descriptors form a contiguous prefix of the pending run — once the scan finds
    /// one on the writer's current page, every later one is too.
    /// </summary>
    private void FlushPendingNotOnCurrentPage()
    {
        long firstOffset = _writer.FirstOffset;
        long writerPage = (_writer.Written - firstOffset) / PageLayout.PageSize;
        // Always publish writerPage — caller paths (BeginValueWrite, Build, and
        // MaybeFlushBeforeEntry's now-gated path) rely on _lastWriterPage being
        // current after this returns so the next per-Add gate check is a single cmp.
        _lastWriterPage = writerPage;
        if (_pendingCount == 0) return;

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        ReadOnlySpan<HsstIndexNodeInfo> currentLevel = bufs.CurrentLevel.AsSpan();
        int pendingStart = currentLevel.Length - _pendingCount;

        int firstOnCurrent = pendingStart;
        while (firstOnCurrent < currentLevel.Length)
        {
            long flagAbs = currentLevel[firstOnCurrent].ChildOffset + _baseOffset;
            long flagPage = (flagAbs - firstOffset) / PageLayout.PageSize;
            if (flagPage == writerPage) break;
            firstOnCurrent++;
        }

        int directCount = firstOnCurrent - pendingStart;
        if (directCount == 0) return;

        _pendingCount -= directCount;

        // Recompute PendingMaxSepLen over the surviving pending range. The
        // stranded descriptors that contributed to the previous max are gone,
        // and the surviving entries' cp values in CommonPrefixArr are untouched.
        // This rescan runs at most once per writer-page transition (and only when
        // stranded entries existed); the per-Add scan it replaces is gone.
        byte newMax = 0;
        if (_keyLength > 0)
        {
            ReadOnlySpan<byte> cpArr = bufs.CommonPrefixArr.AsSpan();
            int firstSurvivingEntry = _entryCount - _pendingCount;
            for (int i = firstSurvivingEntry; i < _entryCount; i++)
            {
                byte sl = (byte)Math.Min(cpArr[i] + 1, _keyLength);
                if (sl > newMax) newMax = sl;
            }
        }
        bufs.PendingMaxSepLen = newMax;
    }

}
