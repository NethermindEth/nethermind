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
/// Index-region construction for <see cref="HsstBTreeBuilder{TWriter}"/> — see
/// the partial in <c>HsstBTreeBuilder.cs</c> for the data-region (entry-add) phase.
/// </summary>
public ref partial struct HsstBTreeBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    // ─────────── Index-region construction ───────────
    //
    // Builds the B-tree index region. Consumes the per-build state already prepared
    // by the data-region phase above (CurrentLevel / CurrentLevelFirstKeys descriptor
    // lists, CommonPrefixArr) and produces a complete index region where the root
    // index is the last block (readable from end via the trailer).
    //
    // Per-key state during this build phase is one <c>long</c> position. Per-entry
    // common-prefix lengths against the prior entry's key are precomputed online in
    // <see cref="EmitEntryBookkeeping"/> into <c>_buffers.CommonPrefixArr</c>; leaf separators
    // are derived as <c>min(commonPrefix + 1, currKeyLen)</c>. Internal-node
    // separators are derived the same way — adjacency of <see cref="HsstIndexNodeInfo"/>
    // ranges means <c>commonPrefixArr[curr.FirstEntry]</c> already holds the LCP
    // between the left-subtree's last key and the right-subtree's first key; the
    // separator bytes are taken from the right-subtree's first key, sourced from the
    // parallel <see cref="HsstBTreeBuilderBuffers.CurrentLevelFirstKeys"/> list. The
    // buffered first-keys avoid reaching back into the already-written data region
    // for a key whose bytes may straddle a 4 KiB page boundary.

    private const int MaxKeyLen = 255;

    /// <summary>Hard upper bound on children per intermediate node (fan-out) — sanity cap
    /// only; the byte threshold (<see cref="MaxIntermediateBytes"/>) is the normal binding
    /// constraint.</summary>
    private const int MaxIntermediateEntries = 2048;

    /// <summary>Byte budget per intermediate node — accumulation stops when the next child
    /// would push the estimated node size over this threshold. Higher values flatten the
    /// tree (fewer levels = fewer cache misses per lookup) at the cost of a larger per-node
    /// binary search. Set to <see cref="PageLayout.PageSize"/> so each intermediate fits in a
    /// single page-aligned pin window.</summary>
    private const int MaxIntermediateBytes = PageLayout.PageSize;

    /// <summary>Minimum children per intermediate node — accumulation always reaches this
    /// before the dynamic-split heuristics (max-sep growth, value-slot widening, 4 KiB
    /// page-crossing) are allowed to fire.</summary>
    private const int MinIntermediateChildren = 4;

    /// <summary>
    /// Cap on the common-key-prefix length stored in node metadata. Bounded by
    /// the u8 prefix-length byte in the fixed footer; 128 keeps prefix blocks
    /// small enough that <see cref="HsstBTreeReader"/>'s footer probe-window
    /// reads them in one shot.
    /// </summary>
    internal const int MaxCommonKeyPrefixLen = 128;

    /// <summary>
    /// The index-node layout chosen by <see cref="ComputeLayout"/>: common-key-prefix length
    /// plus (KeyType, KeySlotSize) and the little-endian flag.
    /// </summary>
    /// <param name="CommonKeyPrefixLen">Post-gating LCP. 0 if not worth stripping.</param>
    /// <param name="KeyType">0=Variable, 1=Uniform.</param>
    /// <param name="KeySlotSize">Post-strip slot size for Uniform; 0 for Variable.</param>
    /// <param name="KeyLittleEndian">
    /// When true, callers should set <c>BTreeNodeMetadata.IsKeyLittleEndian</c> so each
    /// fixed-width key slot is byte-reversed on disk (Flags bit 5). Set for the SIMD-eligible
    /// shapes: Uniform with <see cref="KeySlotSize"/> ∈ {2,4,8} and Variable (whose 2-byte
    /// prefixArr is uniformly LE-encoded).
    /// </param>
    internal readonly record struct LayoutPlan(
        int CommonKeyPrefixLen,
        int KeyType,
        int KeySlotSize,
        bool KeyLittleEndian);

    /// <summary>
    /// Decide the tightest index-node layout — common-key-prefix length plus
    /// (KeyType, KeySlotSize) — for a node whose per-entry separator lengths are supplied in
    /// <paramref name="lengths"/>. The cross-entry LCP is derived as the chain-min of
    /// <paramref name="commonPrefixArr"/> over the entry range the <paramref name="children"/>
    /// cover (by construction <c>commonPrefixArr[curr.FirstEntry]</c> is the LCP between adjacent
    /// subtrees, so the chain-min is the prefix shared by every key in the node). The layout is
    /// chosen against post-strip (effective) lengths so a node whose mixed-length keys collapse to
    /// fixed-width suffixes after stripping gets the tightest layout the data supports.
    /// </summary>
    /// <param name="lengths">Per-entry separator length. Length determines count.</param>
    /// <param name="children">Child descriptors covering this node's entry range; count matches <paramref name="lengths"/>.</param>
    /// <param name="commonPrefixArr">Shared per-entry LCP array, indexed by global entry index.</param>
    /// <param name="keyLength">
    /// Per-key byte budget — the uniform key length declared by the HSST. Bounds how far a short
    /// uniform separator can be widened to a SIMD-eligible {2,4,8} slot (the writer pads the slot
    /// from key data past the natural separator).
    /// </param>
    /// <returns>The chosen layout — see <see cref="LayoutPlan"/>.</returns>
    internal static LayoutPlan ComputeLayout(
        ReadOnlySpan<int> lengths,
        scoped ReadOnlySpan<HsstIndexNodeInfo> children,
        scoped ReadOnlySpan<byte> commonPrefixArr,
        int keyLength)
    {
        int count = lengths.Length;
        if (count == 0)
            return default;

        // Cross-entry LCP: chain-min of commonPrefixArr over [first.FirstEntry + 1 .. last.LastEntry].
        // The index-0 boundary against the (nonexistent) prior subtree is conventionally 0; a
        // single-child range is empty and leaves crossEntryLcp at MaxKeyLen (clamped to minLen below).
        int crossEntryLcp = MaxKeyLen;
        int rangeStart = children[0].FirstEntry;
        int rangeEnd = children[^1].LastEntry;
        for (int j = rangeStart + 1; j <= rangeEnd; j++)
        {
            byte v = commonPrefixArr[j];
            if (v < crossEntryLcp) crossEntryLcp = v;
        }

        int firstLen = lengths[0];
        int minLen = firstLen;
        int maxLen = firstLen;

        for (int i = 1; i < count; i++)
        {
            int len = lengths[i];
            if (len < minLen) minLen = len;
            if (len > maxLen) maxLen = len;
        }

        bool allSameLen = minLen == maxLen;

        // lcp = the common prefix stripped from every separator and stored once in the node
        // header, capped (each line below) by:
        //  (1) maxLen, the longest separator — can't strip more than a separator holds, or the
        //      post-strip residual (effMaxLen) would go negative. Also bounds the single-child
        //      MaxKeyLen sentinel (crossEntryLcp over an empty adjacency range).
        //  (2) keyLength - 1, so every Uniform slot keeps at least one byte.
        //  (3) MaxCommonKeyPrefixLen, the u8 prefix-length header field.
        // A separator shorter than lcp (only the first one can be — see the crossEntryLcp loop
        // above) is not handled here: the Variable writer clamps that entry's stored length to 0,
        // and Uniform reads a fixed slot from the full key regardless of the separator length.
        int lcp = Math.Min(crossEntryLcp, maxLen);
        if (lcp > keyLength - 1) lcp = keyLength - 1;
        if (lcp > MaxCommonKeyPrefixLen) lcp = MaxCommonKeyPrefixLen;

        // Strip-gate: strictly positive net savings.
        // Block cost = 1 + lcp; per-entry saving = lcp; net = lcp * (count - 1) - 1.
        if (lcp <= 0 || lcp * (count - 1) - 1 <= 0)
            lcp = 0;

        // KeyType selection on effective (post-strip) lengths. Two outcomes:
        //   * Uniform: every slot is the same fixed width; mixed-length entries pad
        //     from the key data section past the natural separator.
        //   * Variable: only chosen when effMaxLen > 8 and lengths actually vary,
        //     where padding every entry up to effMaxLen would cost more than the
        //     Variable layout's 4 B/entry overhead. The splitter's `gap > 8` quality
        //     gate keeps within-leaf length variance small, so this path is rare.
        int effMaxLen = maxLen - lcp;

        int keyType;
        int keySlotSize;
        if (allSameLen || effMaxLen <= 8)
        {
            keyType = 1;
            // Slot widening, applied AFTER the common-prefix strip: snap the post-strip
            // residual up to a power-of-2 SIMD width when the remaining per-key budget allows
            // (the writer pads each short slot from key data past its natural separator).
            keySlotSize = WidenedSlotWidth(effMaxLen, keyLength - lcp);
        }
        else
        {
            keyType = 0;
            keySlotSize = 0;
        }

        // Auto-enable LE storage where the SIMD/integer-compare floor scan can exploit it:
        // Uniform 2/4/8, and Variable (prefixArr is uniformly 2B/slot).
        bool keyLittleEndian =
            keyType == 0 ||
            (keyType == 1 && keySlotSize is 2 or 4 or 8);

        return new LayoutPlan(lcp, keyType, keySlotSize, keyLittleEndian);
    }

    /// <summary>
    /// Slot-widening rule shared by <see cref="ComputeLayout"/> and the split heuristic in
    /// <see cref="ChooseIntermediateChildCount"/> that sizes a node before planning it: the
    /// SIMD-eligible Uniform slot width a node whose longest separator is
    /// <paramref name="maxLen"/> bytes is widened up to — {2, 4, 8} when the per-key
    /// <paramref name="keyLength"/> budget allows — or <paramref name="maxLen"/> unchanged
    /// when no widening applies (longer than 8 bytes, or the budget is too tight).
    /// </summary>
    internal static int WidenedSlotWidth(int maxLen, int keyLength) =>
        maxLen <= 2 && keyLength >= 2 ? 2 :
        maxLen <= 4 && keyLength >= 4 ? 4 :
        maxLen <= 8 && keyLength >= 8 ? 8 :
        maxLen;

    /// <summary>
    /// Build the B-tree index region via <c>_writer</c>. The absolute data-region
    /// start offset (= dataLen) is needed to compute child offsets. Returns the byte
    /// length of the root node — the caller writes the trailer
    /// <c>[RootPrefix bytes][RootPrefixLen u8][RootSize u16][KeyLength u8][IndexType u8]</c>
    /// using that value plus <c>_rootPrefixLen</c> and the bytes obtained from
    /// <see cref="CopyRootPrefixBytes"/> so readers can locate the root from the HSST
    /// end and supply the root's prefix bytes when parsing its header.
    /// </summary>
    private int BuildIndex(long absoluteIndexStart)
    {
        long startWritten = _writer.Written;
        long firstOffset = _writer.FirstOffset;

        // Root prefix tracking: the final node emitted is the root.
        _rootPrefixLen = 0;
        ref HsstBTreeBuilderBuffers bufs = ref _buffers;
        if (_entryCount == 0)
        {
            // Empty index: write a single empty index node.
            return WriteEmptyIndexNode();
        }

        ReadOnlySpan<byte> commonPrefixArr = bufs.CommonPrefixArr.AsSpan();

        // CurrentLevel is pre-populated by the inline-leaf emission in the data-region
        // phase (page-local leaves pushed during Add, plus a final trigger 3 flush at
        // Build start). BuildIndex is purely the intermediate-construction loop. The
        // parallel CurrentLevelFirstKeys list carries each descriptor's first-entry
        // full key in matching order so this loop never re-reads the data section.
        ref NativeMemoryList<HsstIndexNodeInfo> currentNative = ref bufs.CurrentLevel;
        ref NativeMemoryList<HsstIndexNodeInfo> nextNative = ref bufs.NextLevel;
        ref NativeMemoryList<byte> currentFirstKeys = ref bufs.CurrentLevelFirstKeys;
        ref NativeMemoryList<byte> nextFirstKeys = ref bufs.NextLevelFirstKeys;

        int lastNodeLen = 0;
        int lastNodePrefixLen = 0;

        // If level 0 has a single node (one page-local leaf written by trigger 3), it
        // IS the root — return its byte length without writing any intermediate. The
        // leaf was just written above, so its bytes occupy
        // <c>[only.ChildOffset, absoluteIndexStart)</c>. The leaf descriptor carries
        // the planner-picked prefix length recorded at MaybeEmitInlineLeaf time; that
        // becomes the root's prefix length for the trailer.
        if (currentNative.Count == 1)
        {
            HsstIndexNodeInfo only = currentNative.AsSpan()[0];
            _rootPrefixLen = only.PrefixLen;
            CaptureRootFirstKey(ref bufs, currentFirstKeys.AsSpan());
            return checked((int)(absoluteIndexStart - only.ChildOffset));
        }

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
                    _writer.Written, firstOffset,
                    commonPrefixArr);
                ReadOnlySpan<HsstIndexNodeInfo> children = current.Slice(childIdx, childCount);
                ReadOnlySpan<byte> childFirstKeys = _keyLength == 0
                    ? default
                    : currentFirstKeysSpan.Slice(childIdx * _keyLength, childCount * _keyLength);

                // Pad to a fresh page when close to the boundary so each intermediate
                // starts page-aligned. Padding bytes are inert — parent nodes record
                // exact child offsets, so readers never look at the gap.
                MaybePadToNextPage();

                long nodeStart = _writer.Written;
                long relativeStart = nodeStart - startWritten;
                WriteIndexNode(children, childFirstKeys, commonPrefixArr, out int intermediatePrefixLen);
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

    /// <summary>Cache the root's full first-key in <see cref="HsstBTreeBuilderBuffers.RootFirstKey"/> so <see cref="CopyRootPrefixBytes"/> can emit the trailer's RootPrefix without re-reading the data section.</summary>
    private static void CaptureRootFirstKey(scoped ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> finalLevelKeys)
    {
        if (finalLevelKeys.Length == 0) return;
        // finalLevelKeys is one descriptor's worth of bytes (the root's first key).
        bufs.RootFirstKey.Clear();
        bufs.RootFirstKey.AddRange(finalLevelKeys);
    }

    /// <summary>Copy the root's common-key-prefix bytes into <paramref name="dest"/> from the cached first-key, returning the byte count (<c>_rootPrefixLen</c>).</summary>
    private int CopyRootPrefixBytes(scoped Span<byte> dest)
    {
        if (_rootPrefixLen == 0) return 0;
        ReadOnlySpan<byte> rootFirstKey = _buffers.RootFirstKey.AsSpan();
        if (rootFirstKey.Length < _rootPrefixLen)
            throw new InvalidOperationException("Root first-key cache not populated by BuildIndex.");
        rootFirstKey[.._rootPrefixLen].CopyTo(dest);
        return _rootPrefixLen;
    }

    private int WriteEmptyIndexNode()
    {
        long nodeStart = _writer.Written;
        BTreeNodeWriter<TWriter>.WriteEmpty(ref _writer, new BTreeNodeMetadata
        {
            NodeKind = BTreeNodeKind.Intermediate,
            KeyType = 0,
            BaseOffset = 0,
            KeySlotSize = 1,
            // Empty node has no values; ValueSlotSize = 2 is the smallest supported width
            // and the size that gets encoded into the Flags byte. The values section is
            // 0 bytes either way (KeyCount * ValueSize = 0 * 2 = 0).
            ValueSlotSize = 2,
        });
        return checked((int)(_writer.Written - nodeStart));
    }

    /// <summary>
    /// Unified node writer: emit a <see cref="BTreeNodeKind.Intermediate"/> BTreeNode
    /// node covering the given <paramref name="children"/>. Used for both inline page-local
    /// nodes (each child wraps a single entry; pushed from
    /// <see cref="MaybeEmitInlineLeaf"/>) and inner nodes (each child is a previously-emitted
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
        scoped ReadOnlySpan<byte> commonPrefixArr,
        out int nodePrefixLen)
    {
        int count = children.Length;
        ref HsstBTreeBuilderBuffers bufs = ref _buffers;

        // Per-child separator length (see SeparatorLength). Backed by a reused list so
        // back-to-back Builds reuse the buffer.
        NativeMemoryList<int> sepLengthsList = bufs.IndexSepLengthsScratch;
        sepLengthsList.Clear();
        for (int i = 0; i < count; i++)
            sepLengthsList.Add(SeparatorLength(children[i], commonPrefixArr));
        Span<int> sepLengths = sepLengthsList.AsSpan();

        // ComputeLayout derives the cross-entry LCP from the shared per-entry LCP array
        // (cp[entry j] is identical at every level by construction) over the children's range.
        LayoutPlan plan = ComputeLayout(sepLengths, children, commonPrefixArr, _keyLength);
        int prefixLen = plan.CommonKeyPrefixLen;
        int keyType = plan.KeyType;
        int keySlotSize = plan.KeySlotSize;
        bool keyLittleEndian = plan.KeyLittleEndian;

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
        int valueSlotSize = HsstValueSlot.MinBytesFor(maxOff - baseOffset);

        Span<byte> commonPrefixBuf = stackalloc byte[prefixLen];
        if (prefixLen > 0)
        {
            // Leftmost child's first-key bytes live at the start of childFirstKeys.
            childFirstKeys[..prefixLen].CopyTo(commonPrefixBuf);
        }

        // Pre-encode all child offsets as a flat values block: count * valueSlotSize bytes,
        // each entry already delta-adjusted against baseOffset and written LE. BTreeNodeWriter
        // reads keys in-place from childFirstKeys and values stride-wise from this block,
        // so no per-entry staging copy is needed.
        NativeMemoryList<byte> valueScratch = bufs.ValueScratch;
        valueScratch.Clear();
        valueScratch.EnsureCapacity(count * valueSlotSize);
        for (int i = 0; i < count; i++)
        {
            long delta = children[i].ChildOffset - baseOffset;
            for (int b = 0; b < valueSlotSize; b++)
                valueScratch.Add((byte)(delta >> (b * 8)));
        }
        Span<byte> values = valueScratch.AsSpan();

        BTreeNodeWriter<TWriter>.Write(
            ref _writer,
            new BTreeNodeMetadata
            {
                NodeKind = BTreeNodeKind.Intermediate,
                KeyType = keyType,
                BaseOffset = (ulong)baseOffset,
                KeySlotSize = keySlotSize,
                ValueSlotSize = valueSlotSize,
                IsKeyLittleEndian = keyLittleEndian,
            },
            count,
            childFirstKeys,
            fullKeyLength: _keyLength,
            prefixLen,
            sepLengths: keyType == 1 ? default : sepLengths,
            values,
            commonPrefixBuf);
        nodePrefixLen = prefixLen;
    }

    /// <summary>
    /// Stored separator length for <paramref name="child"/>: the larger of the routing length and
    /// the child's own picked prefix. Routing length = <c>min(LCP + 1, keyLength)</c>, where the LCP
    /// (<paramref name="commonPrefixArr"/> at the child's first entry; by the adjacency invariant
    /// that's the prefix shared with the previous subtree's last key) plus one distinguishing byte
    /// is enough to route to the child. The separator is then widened to at least
    /// <see cref="HsstIndexNodeInfo.PrefixLen"/> so the parent slot carries every byte of the child's
    /// own CommonKeyPrefix down to it at descent time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SeparatorLength(HsstIndexNodeInfo child, scoped ReadOnlySpan<byte> commonPrefixArr)
        => Math.Max(Math.Min(commonPrefixArr[child.FirstEntry] + 1, _keyLength), child.PrefixLen);

    /// <summary>Pick the next intermediate node's child count: accumulate values + keys bytes until the next child would exceed <see cref="MaxIntermediateBytes"/>, capped at <see cref="MaxIntermediateEntries"/>, always at least one child.</summary>
    private int ChooseIntermediateChildCount(
        scoped ReadOnlySpan<HsstIndexNodeInfo> level,
        scoped ReadOnlySpan<byte> levelFirstKeys,
        int startIdx,
        long nodeStart, long firstOffset,
        scoped ReadOnlySpan<byte> commonPrefixArr)
    {
        int remaining = level.Length - startIdx;
        int hardMax = Math.Min(MaxIntermediateEntries, remaining);
        if (hardMax <= 1) return hardMax;

        // Slot 0 carries a separator just like every other slot (see SeparatorLength), so seed
        // maxSepLen / commonLen / firstSep from it — the heuristic then models what the writer
        // emits. For a non-first group the boundary LCP can exceed firstChild.PrefixLen.
        HsstIndexNodeInfo firstChild = level[startIdx];
        int firstSepLen = SeparatorLength(firstChild, commonPrefixArr);
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
        int committedValueSlot = HsstValueSlot.MinBytesFor(0);
        // Common-prefix length across separators observed so far. With phantom slot 0 restored
        // the first separator (firstChild) seeds commonLen so the running LCP is meaningful from
        // childCount == 1 onward.
        int commonLen = firstSepLen;
        // firstSep = the first child's first-key prefix, sliced straight from levelFirstKeys
        // (slot startIdx) once; the running group LCP is compared against it. Per-candidate
        // separators are likewise sliced from levelFirstKeys below — no scratch copy needed.
        ReadOnlySpan<byte> firstSep = firstSepLen > 0
            ? levelFirstKeys.Slice(startIdx * _keyLength, firstSepLen)
            : default;

        while (childCount < hardMax)
        {
            // Index in `level` of the candidate child being considered for this group.
            int currentIdx = startIdx + childCount;
            HsstIndexNodeInfo curr = level[currentIdx];
            int sepLen = SeparatorLength(curr, commonPrefixArr);
            // curr's first-key sits at slot currentIdx of levelFirstKeys.
            ReadOnlySpan<byte> sepBuf = sepLen > 0
                ? levelFirstKeys.Slice(currentIdx * _keyLength, sepLen)
                : default;

            long newMaxOff = curr.ChildOffset > maxOff ? curr.ChildOffset : maxOff;
            int valueSlotSize = HsstValueSlot.MinBytesFor(newMaxOff - baseChildOffset);
            int newMaxSepLen = sepLen > maxSepLen ? sepLen : maxSepLen;

            int boundary = Math.Min(commonLen, sepLen);
            int newCommonLen = commonLen == 0
                ? 0
                : firstSep[..boundary].CommonPrefixLength(sepBuf[..boundary]);

            int newCount = childCount + 1;
            // Keys-section size as the writer emits it: a Uniform node packs newCount
            // fixed-width slots, each widened to the planner's {2,4,8} SIMD slot.
            int newKeysBytes = newCount * WidenedSlotWidth(newMaxSepLen, _keyLength);
            // Phantom slot 0 restored: keys array carries newCount real separators
            // (one per child) and values array carries newCount deltas.
            int estimated = newCount * valueSlotSize + newKeysBytes;
            if (estimated > MaxIntermediateBytes) break;

            // Dynamic split heuristics. Once MinIntermediateChildren is reached, break
            // only when:
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
            int next2Idx = currentIdx + 1;
            if (next2Idx < level.Length)
            {
                HsstIndexNodeInfo next2 = level[next2Idx];
                int next2SepLen = SeparatorLength(next2, commonPrefixArr);
                if (next2SepLen > effMaxSepLen) effMaxSepLen = next2SepLen;

                // Chain the running group prefix against next2's separator bytes, capped at
                // min(newCommonLen, next2SepLen).
                int next2Boundary = Math.Min(effCommonLen, next2SepLen);
                sepBuf = next2Boundary > 0
                    ? levelFirstKeys.Slice(next2Idx * _keyLength, next2Boundary)
                    : default;
                effCommonLen = effCommonLen == 0
                    ? 0
                    : firstSep[..next2Boundary].CommonPrefixLength(sepBuf);
            }
            int newEffSepLen = effMaxSepLen - effCommonLen;
            int candidateSize = IntermediateNodeSizeUpperBound(newCount, newKeysBytes, valueSlotSize);
            int committedSize = IntermediateNodeSizeUpperBound(
                childCount,
                childCount * WidenedSlotWidth(maxSepLen, _keyLength),
                committedValueSlot);
            if (childCount >= MinIntermediateChildren &&
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

    // Conservative upper bound on an intermediate node's serialised size with phantom slot 0
    // restored: header + the <paramref name="keysSectionBytes"/> keys section + one value per
    // child. Intermediate values are Uniform child-offset deltas (valueSlotSize bytes each, no
    // length prefix), so for the slot widths these offsets ever use (<= 8 bytes) the value term
    // is exact; a wider slot gets a +2/entry slack for any rounding / Variable-section overhead.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IntermediateNodeSizeUpperBound(int count, int keysSectionBytes, int valueSlotSize)
        => NodeHeaderUpperBound + keysSectionBytes + count * (valueSlotSize <= 8 ? valueSlotSize : valueSlotSize + 2);

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
    /// Companion to <see cref="WouldCrossNewPage"/>: when the writer sits within
    /// <see cref="PageLayout.PadThreshold"/> of the next 4 KiB boundary, pad to it so the following
    /// node doesn't start at the seam and immediately cross. Pad bytes are inert (parent nodes
    /// record exact child offsets, so readers never look at them). Must not run after the final
    /// (root) node — the trailer formula <c>root_start = HSST_end - 4 - rootSize</c> assumes the
    /// trailer abuts the root, so padding between them would offset the computed root start.
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
}
