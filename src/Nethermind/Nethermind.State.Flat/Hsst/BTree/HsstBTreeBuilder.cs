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
/// Builds an HSST (Hierarchical Static Sorted Table) from key-value entries added in sorted key
/// order (no internal sorting). The <c>keyFirst</c> ctor flag selects the data-region layout:
/// <c>false</c> (key-after-value) supports streaming via <see cref="BeginValueWrite"/> /
/// <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long)"/>; <c>true</c> (key-first) requires
/// <see cref="Add(System.ReadOnlySpan{byte},System.ReadOnlySpan{byte})"/>. Wire layout: see
/// <c>Hsst/FORMAT.md</c> ("BTree" / "BTreeKeyFirst" variants).
/// </summary>
public ref partial struct HsstBTreeBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private long _writtenBeforeValue;
    private readonly long _baseOffset;
    private readonly bool _keyFirst;
    private int _keyLength;

    // Root's common-key-prefix length for the trailer, set by BuildIndex (HsstBTreeBuilder.Index.cs);
    // 0 for empty HSSTs. Declared here so all instance fields live in one partial (CS0282).
    private int _rootPrefixLen;

    // Borrowed ref to the caller-owned HsstBTreeBuilderBuffers (a ref field is allowed on this
    // ref struct; HsstBTreeBuilderBuffers is not a ref struct so CS9050 doesn't apply).
    private readonly ref HsstBTreeBuilderBuffers _buffers;

    // Build-wide entry count, incremented once per Add / FinishValueWrite. Also the next entry's
    // index, the CommonPrefixArr valid-range bound, and the FirstEntry/LastEntry stamped on each
    // per-entry <see cref="HsstIndexNodeInfo"/> descriptor.
    private int _entryCount;

    // Trailing <c>_buffers.CurrentLevel</c> descriptors still eligible for a page-local leaf wrap.
    // <see cref="MaybeEmitInlineLeaf"/> wraps the on-page run; <see cref="FlushPendingAsEntries"/> /
    // <see cref="FinalizePendingNotOnCurrentPage"/> just drop the count (descriptors stay in place,
    // sealed as direct Entry children of the intermediate above).
    private int _pendingCount;

    // True once <see cref="MaybeEmitInlineLeaf"/> has written a leaf. Lets <see cref="Build"/>'s
    // single-entry post-process tell a lone unwrapped Entry (needs wrapping for the u16 rootSize)
    // from an already-bounded Leaf.
    private bool _hasEmittedLeaf;

    // Writer page index at the last observation. MaybeFlushBeforeEntry gates
    // FinalizePendingNotOnCurrentPage on it — entries can only strand once the writer page advances,
    // and only Add mutates the writer between consecutive Adds, so the cached value is safe.
    private long _lastWriterPage;

    /// <summary>
    /// Create a builder writing via <paramref name="writer"/> with caller-owned
    /// <paramref name="buffers"/> as scratch (typically <c>using HsstBTreeBuilderBuffers.Container
    /// buffers = new(expectedKeyCount)</c>, then pass <c>ref buffers.Buffers</c>); the caller
    /// disposes it.
    /// </summary>
    /// <remarks>
    /// <paramref name="buffers"/> is reset per build (<see cref="HsstBTreeBuilderBuffers.ResetForBuild"/>)
    /// so it can be reused across back-to-back builds. <paramref name="keyLength"/> is the fixed key
    /// length (0–255) every entry must use, recorded once in the trailer; pass -1 to lock it from the
    /// first <see cref="Add"/>/<see cref="FinishValueWrite"/>, after which mismatches are rejected.
    /// <paramref name="expectedKeyCount"/> pre-sizes the buffers (they still grow on demand).
    /// <paramref name="keyFirst"/> selects the key-first layout (trailer
    /// <see cref="IndexType.BTreeKeyFirst"/>) and makes <see cref="BeginValueWrite"/> throw.
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

    /// <summary>No-op: the caller owns and disposes the <see cref="HsstBTreeBuilderBuffers"/>; kept so <c>using</c> call sites compile.</summary>
    public void Dispose() { }

    /// <summary>
    /// Begin a streaming value: snapshots Written and returns the shared writer. Close with
    /// <see cref="FinishValueWrite(ReadOnlySpan{byte}, long)"/>. Rejected in key-first mode (the
    /// value length must be known up front) — use <see cref="Add"/>.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        if (_keyFirst)
            throw new InvalidOperationException("Key-first BTree requires Add(key, value); BeginValueWrite/FinishValueWrite streaming is not supported.");
        // Trigger 1: seal any pending leaf before a streaming value straddles pages, keeping it
        // colocated with its entries.
        MaybeEmitInlineLeaf();
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish a streaming value of <paramref name="valueLength"/> bytes, counted back from the
    /// current Written; any earlier bytes since <see cref="BeginValueWrite"/> are inert padding
    /// (e.g. to keep the value off a page boundary). <paramref name="key"/> must exceed the previous
    /// key. Rejected in key-first mode — use <see cref="Add"/>.
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

        // metadataPos (relative to _baseOffset) is the entry's flag byte; the reader reads it first
        // to recognize the entry before decoding the value/LEB128.
        long metadataPos = _writer.Written - _baseOffset;

        // Single GetSpan/Advance for the post-value [FlagByte][LEB128][FullKey] trailer; the value
        // bytes were already streamed in via the BeginValueWrite snapshot.
        int lebSize = Leb128.EncodedSize(valueLength);
        int trailerLen = 1 + lebSize + key.Length;
        Span<byte> dest = _writer.GetSpan(trailerLen);
        dest[0] = (byte)BTreeNodeKind.Entry;
        Leb128.Write(dest, 1, valueLength);
        if (key.Length > 0) key.CopyTo(dest.Slice(1 + lebSize, key.Length));
        _writer.Advance(trailerLen);

        // No precomputed LCP on this path — EmitEntryBookkeeping derives it from PrevKeyBuf.
        EmitEntryBookkeeping(ref _buffers, key, metadataPos, precomputedLcp: -1);
    }

    /// <summary>
    /// Add a key-value pair in one call. Best-effort keeps the entry on a single
    /// <see cref="PageLayout.PageSize"/> page via a small leading pad (skipped if it would exceed
    /// <see cref="PageLayout.PadThreshold"/> or the entry is larger than a page). Layout is
    /// <c>[Value][LEB128][FullKey]</c> (recorded position = MetadataStart) in key-after-value mode,
    /// or <c>[FullKey][LEB128][Value]</c> (recorded position = EntryStart) in key-first mode.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        ref HsstBTreeBuilderBuffers bufs = ref _buffers;
        // +1 for the leading per-entry flag byte.
        int lebSize = Leb128.EncodedSize((long)value.Length);
        long entryLen = 1L + key.Length + lebSize + value.Length;
        // LCP vs the prior key, forwarded into EmitEntryBookkeeping so the LCP loop runs once.
        int lcp = MaybeFlushBeforeEntry(ref bufs, key, entryLen);
        // Best-effort page alignment; the entry lands unaligned when it can't be padded.
        TryAlign(entryLen);

        if (_keyLength < 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 255);
            _keyLength = key.Length;
        }
        else if (key.Length != _keyLength)
            throw new ArgumentException($"key length {key.Length} != declared keyLength {_keyLength}", nameof(key));

        // Single GetSpan + Advance per entry; TryAlign's pre-pad has already run, so the slice
        // starts at the post-pad position. Bytes are laid down by local offset, then committed at once.
        int totalLen = 1 + key.Length + lebSize + value.Length;
        long entryStart = _writer.Written - _baseOffset;
        Span<byte> dest = _writer.GetSpan(totalLen);

        long entryPos;
        if (_keyFirst)
        {
            // [FlagByte=Entry][FullKey][LEB128][Value]; EntryStart = flag-byte position. The reader
            // reads the flag, then walks past key + LEB128 to the value.
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
            // [Value][FlagByte=Entry][LEB128][FullKey]; MetadataStart = flag-byte position
            // (= entryStart + value.Length); the reader recovers ValueStart = MetadataStart - ValueLength.
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

        EmitEntryBookkeeping(ref bufs, key, entryPos, lcp);
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
    /// Per-entry bookkeeping shared by <see cref="Add"/> and the streaming
    /// <see cref="FinishValueWrite(System.ReadOnlySpan{byte},long)"/> path: push the entry's index
    /// pointer + first-key onto the level-0 lists, then update LCP / PendingMaxSepLen / PrevKeyBuf.
    /// <paramref name="precomputedLcp"/> is the LCP vs <c>PrevKeyBuf</c> (<c>-1</c> = recompute);
    /// <paramref name="bufs"/> is the caller's already-resolved ref.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitEntryBookkeeping(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, long entryPos, int precomputedLcp)
    {
        // Push the per-entry descriptor (FirstEntry == LastEntry == entryIdx) and its first-key onto
        // level 0; the index phase looks up CommonPrefixArr[FirstEntry] when this becomes a child.
        int entryIdx = _entryCount;
        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(entryPos, entryIdx, entryIdx, prefixLen: 0));
        if (key.Length > 0) bufs.CurrentLevelFirstKeys.AddRange(key);
        _pendingCount++;
        _entryCount++;

        // Record this entry's LCP vs the previous key (appended in entry order, Count == entryIdx).
        int cp = 0;
        if (entryIdx > 0 && _keyLength > 0)
        {
            cp = precomputedLcp >= 0
                ? precomputedLcp
                : MemoryExtensions.CommonPrefixLength(bufs.PrevKeyBuf.AsSpan(), key);
        }
        bufs.CommonPrefixArr.Add((byte)cp);

        // Track max sepLen = min(cp + 1, keyLength) over the pending range so MaybeFlushBeforeEntry
        // skips an O(pending) scan (rebuilt by FinalizePendingNotOnCurrentPage's partial-flush rescan).
        if (_keyLength > 0)
        {
            byte sl = (byte)Math.Min(cp + 1, _keyLength);
            if (sl > bufs.PendingMaxSepLen) bufs.PendingMaxSepLen = sl;
        }

        // PrevKeyBuf seeds the next entry's LCP.
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
        // Trigger 3: flush remaining entries so BuildIndex can skip its leaf phase.
        MaybeEmitInlineLeaf();

        // Single-entry build with no leaf emitted (e.g. the lone value crossed pages, so the on-page
        // filter dropped it from the pending count): the lone CurrentLevel descriptor is a direct
        // Entry whose full record length would overflow the u16 rootSize trailer for large values —
        // wrap it as a 1-entry leaf so the root is a bounded node.
        if (_entryCount == 1 && !_hasEmittedLeaf) WrapLoneEntryAsLeaf();

        long dataSectionSize = _writer.Written - _baseOffset;
        long absoluteIndexStart = dataSectionSize;

        // No data-section read-back: every descriptor carries its first-entry key in
        // CurrentLevelFirstKeys (populated at push time), and BuildIndex propagates first-keys as it
        // walks up the tree.
        int rootSize = BuildIndex(absoluteIndexStart);
        int rootPrefixLen = _rootPrefixLen;

        if ((uint)rootSize > ushort.MaxValue)
            throw new InvalidOperationException($"Root node size {rootSize} exceeds u16 trailer field");
        if ((uint)rootPrefixLen > byte.MaxValue)
            throw new InvalidOperationException($"Root prefix length {rootPrefixLen} exceeds u8 trailer field");

        // Trailer: [RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8],
        // IndexType last. Empty build (_keyLength still -1) records KeyLength = RootPrefixLen = 0;
        // CopyRootPrefixBytes writes the prefix straight into the span head.
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
    /// Trigger 2 (page-boundary fit): flush the pending set as a leaf when the next entry plus that
    /// leaf would straddle the current 4 KiB page. Returns the LCP between <paramref name="key"/> and
    /// PrevKeyBuf (<c>-1</c> when none) so the caller can thread it into EmitEntryBookkeeping.
    /// </summary>
    private int MaybeFlushBeforeEntry(ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> key, long entryLen)
    {
        // LCP computed once (reused for the leaf-fit estimate and returned). Uses PrevKeyBuf so it
        // survives flushes that clear the pending range and a prior entry stranded onto a past page.
        int lcp = -1;
        if (_keyLength > 0 && key.Length == _keyLength && bufs.PrevKeyBuf.Count >= _keyLength)
        {
            lcp = MemoryExtensions.CommonPrefixLength(bufs.PrevKeyBuf.AsSpan(), key);
        }

        int pending = _pendingCount;
        if (pending < 1) return lcp;
        if (_keyLength <= 0) return lcp;

        // Stranded-entry prune only matters when the writer page advanced since the last Add (only
        // Add mutates the writer between Adds). FinalizePendingNotOnCurrentPage updates _lastWriterPage.
        long writerPage = (_writer.Written - _writer.FirstOffset) / PageLayout.PageSize;
        if (writerPage != _lastWriterPage)
        {
            FinalizePendingNotOnCurrentPage();
            pending = _pendingCount;
            if (pending < 1) return lcp;
        }

        int newSepLen = lcp >= 0 ? Math.Min(lcp + 1, _keyLength) : _keyLength;

        // Max pending sep length is maintained incrementally by EmitEntryBookkeeping.
        int maxSepLen = bufs.PendingMaxSepLen;
        int maxSepWithNew = Math.Max(maxSepLen, newSepLen);

        // Variable-key leaf size upper bound (matches BTreeNodeWriter): 12B header + 4B/entry
        // (u16 prefixArr + u16 offsetArr) + 2B/entry value slot + max(0, sepLen - 2) tail/entry.
        int estLeafTailPer = Math.Max(0, maxSepWithNew - 2);
        int estLeafPerEntry = 4 + PageLocalLeafValueSlotBytes + estLeafTailPer;
        int estLeaf = PageLocalLeafHeaderBytes + (pending + 1) * estLeafPerEntry;

        long inPage = (_writer.Written - _writer.FirstOffset) & PageLayout.PageMask;
        long remaining = PageLayout.PageSize - inPage;
        if (entryLen + estLeaf <= remaining) return lcp;

        // Doesn't fit: seal pending now. If even the current K-entry leaf won't fit in the page
        // remainder (e.g. the prior entry left the page nearly full), don't write a cross-page leaf
        // that loses the page-locality it exists for — drop the pending count so the entries become
        // direct children of the future intermediate. No force-pad: the leaf-fit check plus the
        // page-prune at the top handle the K=1 trap on the next iteration.
        int estLeafActualTailPer = Math.Max(0, maxSepLen - 2);
        int estLeafActualPerEntry = 4 + PageLocalLeafValueSlotBytes + estLeafActualTailPer;
        int estLeafActual = PageLocalLeafHeaderBytes + pending * estLeafActualPerEntry;
        if (estLeafActual > remaining)
        {
            _pendingCount = 0;
            _buffers.PendingMaxSepLen = 0;
        }
        else
            MaybeEmitInlineLeaf();

        return lcp;
    }

    private const int PageLocalLeafHeaderBytes = 12;
    private const int PageLocalLeafValueSlotBytes = 2;

    /// <summary>
    /// Wrap the trailing on-page pending run of Entry descriptors in <c>_buffers.CurrentLevel</c> as
    /// one page-local leaf (popping them, pushing the leaf) and clear <see cref="_pendingCount"/>.
    /// No-op when nothing is pending.
    /// </summary>
    private void MaybeEmitInlineLeaf()
    {
        if (_pendingCount == 0) return;

        // Drop off-page pending entries (they stay as sealed Entry descriptors); also refreshes
        // _lastWriterPage so the next per-Add gate check is a single cmp.
        FinalizePendingNotOnCurrentPage();
        if (_pendingCount == 0) return;

        // Singleton: the lone Entry descriptor is already on CurrentLevel — just seal.
        if (_pendingCount == 1)
        {
            _pendingCount = 0;
            _buffers.PendingMaxSepLen = 0;
            return;
        }

        long nodeStart = _writer.Written - _baseOffset;

        ref HsstBTreeBuilderBuffers bufs = ref _buffers;
        int count = _pendingCount;

        // The pending descriptors and their first-keys are the trailing slices of CurrentLevel /
        // CurrentLevelFirstKeys — pass them straight to WriteIndexNode (no per-entry stackalloc).
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

        // Pop the entry descriptors, push the leaf. The leftmost popped key is also the leaf's
        // first-key, so a single Truncate keeps it and drops the (count - 1) following key blocks.
        bufs.CurrentLevel.Truncate(childrenStart);
        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(nodeStart, firstEntryIdx, lastEntryIdx, leafPrefixLen));
        if (_keyLength > 0) bufs.CurrentLevelFirstKeys.Truncate(keysStart + _keyLength);

        _pendingCount = 0;
        _hasEmittedLeaf = true;
        bufs.PendingMaxSepLen = 0;
    }

    /// <summary>
    /// Build-time post-process for a single-entry HSST with no leaf emitted: wrap the lone direct
    /// Entry descriptor as a 1-entry leaf so the root is bounded (a direct Entry root overflows the
    /// u16 rootSize trailer past ~64 KiB). Unlike <see cref="MaybeEmitInlineLeaf"/>, bypasses the
    /// on-page filter — a cross-page leaf is acceptable here.
    /// </summary>
    private void WrapLoneEntryAsLeaf()
    {
        ref HsstBTreeBuilderBuffers bufs = ref _buffers;
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

        // Replace the lone Entry with the leaf; its first-key block stays in place.
        bufs.CurrentLevel.Truncate(0);
        bufs.CurrentLevel.Add(new HsstIndexNodeInfo(nodeStart, firstEntryIdx, lastEntryIdx, leafPrefixLen));
        _hasEmittedLeaf = true;
    }

    /// <summary>
    /// Trim the pending run to descriptors whose flag byte sits on the writer's current page; older
    /// (stranded) descriptors become sealed direct Entry children of the intermediate above (no data
    /// movement). Refreshes <see cref="_lastWriterPage"/>. Positions are monotonic, so the stranded
    /// descriptors form a contiguous prefix of the run.
    /// </summary>
    private void FinalizePendingNotOnCurrentPage()
    {
        long firstOffset = _writer.FirstOffset;
        long writerPage = (_writer.Written - firstOffset) / PageLayout.PageSize;
        // Always publish writerPage so the next per-Add gate check is a single cmp (callers rely on
        // _lastWriterPage being current after this returns).
        _lastWriterPage = writerPage;
        if (_pendingCount == 0) return;

        ref HsstBTreeBuilderBuffers bufs = ref _buffers;
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

        // Recompute PendingMaxSepLen over the surviving range (the stranded descriptors that may
        // have held the previous max are gone). Runs at most once per writer-page transition.
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
