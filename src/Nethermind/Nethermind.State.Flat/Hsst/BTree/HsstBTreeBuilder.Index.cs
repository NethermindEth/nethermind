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
/// Index-region construction for <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/> — see
/// the partial in <c>HsstBTreeBuilder.cs</c> for the data-region (entry-add) phase.
/// </summary>
public ref partial struct HsstBTreeBuilder<TWriter, TReader, TPin>
    where TWriter : IByteBufferWriterWithReader<TReader, TPin>
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
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
    // <see cref="EmitEntryBookkeeping"/> into <c>Buffers.CommonPrefixArr</c>; leaf separators
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
    /// binary search. Set to one 4 KiB page so each intermediate fits in a single
    /// page-aligned pin window.</summary>
    private const int MaxIntermediateBytes = 4096;

    /// <summary>Minimum children per intermediate node — accumulation always reaches this
    /// before the dynamic-split heuristics (max-sep growth, value-slot widening, 4 KiB
    /// page-crossing) are allowed to fire.</summary>
    private const int MinIntermediateChildren = 16;

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
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        if (_entryCount == 0)
        {
            // Empty index: write a single empty index node.
            return WriteEmptyIndexNode();
        }

        bufs.EnsureValueScratchCapacity(Math.Max(64, MaxIntermediateEntries * 8));
        byte[] valueScratchArr = bufs.ValueScratch!;
        byte[] commonPrefixArr = bufs.CommonPrefixArr!;

        // CurrentLevel is pre-populated by the inline-leaf emission in the data-region
        // phase (page-local leaves pushed during Add, plus a final trigger 3 flush at
        // Build start). BuildIndex is purely the intermediate-construction loop. The
        // parallel CurrentLevelFirstKeys list carries each descriptor's first-entry
        // full key in matching order so this loop never re-reads the data section.
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
        // the planner-picked prefix length recorded at MaybeEmitInlineLeaf time; that
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

    /// <summary>Cache the root's full first-key in <see cref="HsstBTreeBuilderBuffers.RootFirstKey"/> so <see cref="CopyRootPrefixBytes"/> can emit the trailer's RootPrefix without re-reading the data section.</summary>
    private static void CaptureRootFirstKey(scoped ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> finalLevelKeys)
    {
        if (finalLevelKeys.Length == 0) return;
        bufs.EnsureRootFirstKeyCapacity(finalLevelKeys.Length);
        // finalLevelKeys.Length is one descriptor's worth of bytes (the root); copying
        // every byte is correct because RootFirstKey is sized to at least that span.
        finalLevelKeys.CopyTo(bufs.RootFirstKey);
    }

    /// <summary>Copy the root's common-key-prefix bytes into <paramref name="dest"/> from the cached first-key, returning the byte count (<c>_rootPrefixLen</c>).</summary>
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
        bufs.EnsureIndexSepLengthsCapacity(count);
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

        BTreeNodeLayoutPlan plan = BTreeNodeLayoutPlanner.Plan(sepLengths, crossEntryLcp, _keyLength);
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
        Span<byte> values = valueScratch[..(count * valueSlotSize)];
        for (int i = 0; i < count; i++)
        {
            long delta = children[i].ChildOffset - baseOffset;
            int off = i * valueSlotSize;
            for (int b = 0; b < valueSlotSize; b++)
                values[off + b] = (byte)(delta >> (b * 8));
        }

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

    /// <summary>Chain-min of <c>commonPrefixArr</c> over the entry range covered by <paramref name="children"/>; the index-0 boundary against the (nonexistent) prior subtree is conventionally 0.</summary>
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

    /// <summary>Pick the next intermediate node's child count: accumulate values + keys bytes until the next child would exceed <see cref="MaxIntermediateBytes"/>, capped at <see cref="MaxIntermediateEntries"/>, always at least one child.</summary>
    private int ChooseIntermediateChildCount(
        scoped ReadOnlySpan<HsstIndexNodeInfo> level,
        scoped ReadOnlySpan<byte> levelFirstKeys,
        int childIdx,
        long nodeStart, long firstOffset,
        byte[] commonPrefixArr)
    {
        int remaining = level.Length - childIdx;
        int hardMax = Math.Min(MaxIntermediateEntries, remaining);
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
        int committedValueSlot = HsstValueSlot.MinBytesFor(0);
        // Common-prefix length across separators observed so far. With phantom slot 0
        // restored the first separator (firstChild) seeds commonLen and firstSep so the
        // running LCP is meaningful from childCount == 1 onward. firstSep / sepBuf live
        // on the pooled buffers struct so back-to-back Builds reuse the rent instead of
        // re-stackallocating 510 bytes per ChooseIntermediateChildCount call.
        int commonLen = firstSepLen;
        ref HsstBTreeBuilderBuffers bufs = ref Buffers;
        bufs.EnsureIndexFirstSepCapacity(MaxKeyLen);
        bufs.EnsureIndexSepBufCapacity(MaxKeyLen);
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
            int valueSlotSize = HsstValueSlot.MinBytesFor(newMaxOff - baseChildOffset);
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
}
