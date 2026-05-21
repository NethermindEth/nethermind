// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds the B-tree index region for an HSST block.
/// Takes <c>entryPositions</c> plus the parallel
/// <see cref="HsstBTreeBuilderBuffers.CurrentLevel"/> /
/// <see cref="HsstBTreeBuilderBuffers.CurrentLevelFirstKeys"/> lists prepared by
/// <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/> and produces a complete
/// index region where the root index is the last block (readable from end via the
/// trailer).
///
/// Per-key state during this build phase is one <c>long</c> position. Per-entry
/// common prefix lengths against the prior entry's key are precomputed online during
/// <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}.OnEntryAdded"/> into
/// <c>Buffers.CommonPrefixArr</c>; leaf separators are derived as
/// <c>min(commonPrefix + 1, currKeyLen)</c>. Internal-node separators are derived
/// the same way — adjacency of <c>NodeInfo</c> ranges means
/// <c>commonPrefixArr[curr.FirstEntry]</c> already holds the LCP between the
/// left-subtree's last key and the right-subtree's first key; the separator bytes
/// are taken from the right-subtree's first key, sourced from the parallel
/// <see cref="HsstBTreeBuilderBuffers.CurrentLevelFirstKeys"/> list (each descriptor
/// in the level carries its first-entry's full key at the matching position). The
/// buffered first-keys avoid reaching back into the already-written data region for
/// a 20-byte key whose bytes may straddle a 4 KiB page boundary.
/// </summary>
public ref struct HsstIndexBuilder<TWriter, TReader, TPin>
    where TWriter : IByteBufferWriterWithReader<TReader, TPin>
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    private const int MaxKeyLen = 255;

    private ref TWriter _writer;
    private readonly ReadOnlySpan<long> _entryPositions;
    // Fixed key length for every entry (HsstBTreeBuilder enforces uniformity, and the
    // HSST trailer records the same value so readers don't need a per-entry length
    // byte). Used directly wherever we previously tracked minKeyLen — those collapse
    // to this single scalar.
    private readonly int _keyLength;
    // Pointer to the caller-supplied buffers struct holding the work arrays/lists
    // (PendingKeys, EntryPositions, CommonPrefixArr, CurrentLevel/NextLevel,
    // CurrentLevelFirstKeys/NextLevelFirstKeys, ValueScratch, RootFirstKey).
    // Stored as void* because HsstBTreeBuilderBuffers is a ref struct and therefore not
    // eligible for ordinary T* / managed-pointer fields.
    private readonly unsafe void* _buffersPtr;

    public unsafe HsstIndexBuilder(ref TWriter writer, ReadOnlySpan<long> entryPositions, int keyLength, scoped ref HsstBTreeBuilderBuffers buffers)
    {
        _writer = ref writer;
        _entryPositions = entryPositions;
        _keyLength = keyLength;
        _buffersPtr = Unsafe.AsPointer(ref buffers);
    }

    private unsafe ref HsstBTreeBuilderBuffers Buffers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.AsRef<HsstBTreeBuilderBuffers>(_buffersPtr);
    }

    /// <summary>
    /// Build B-tree index via writer.
    /// The absolute data region start offset (= 1 + dataLen) is needed to compute child offsets.
    /// Returns the byte length of the root node — the caller writes the
    /// <c>[RootPrefix bytes][RootPrefixLen u8][RootSize u16][KeyLength u8][IndexType u8]</c>
    /// trailer using that value plus <see cref="RootPrefixLen"/> and the bytes obtained from
    /// <see cref="CopyRootPrefixBytes"/> so readers can locate the root from the HSST end
    /// and supply the root's prefix bytes when parsing its header.
    /// </summary>
    public unsafe int Build(long absoluteIndexStart,
        int maxLeafEntries = HsstBTreeOptions.DefaultMaxLeafEntries,
        int maxIntermediateEntries = HsstBTreeOptions.DefaultMaxIntermediateEntries,
        int minLeafEntries = HsstBTreeOptions.DefaultMinLeafEntries,
        int maxIntermediateBytes = HsstBTreeOptions.DefaultMaxIntermediateBytes,
        int minIntermediateChildren = HsstBTreeOptions.DefaultMinIntermediateChildren,
        int minIntermediateBytes = HsstBTreeOptions.DefaultMinIntermediateBytes)
    {
        long startWritten = _writer.Written;
        long firstOffset = _writer.FirstOffset;

        // Root prefix tracking: the final node emitted is the root.
        _rootPrefixLen = 0;
        if (_entryPositions.Length == 0)
        {
            // Empty index: write a single empty index node.
            return WriteEmptyIndexNode();
        }

        if (minIntermediateChildren > maxIntermediateEntries) minIntermediateChildren = maxIntermediateEntries;
        if (minIntermediateChildren < 1) minIntermediateChildren = 1;
        if (minIntermediateBytes < 0) minIntermediateBytes = 0;
        if (minIntermediateBytes > maxIntermediateBytes) minIntermediateBytes = maxIntermediateBytes;

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;

        int valueScratchEntries = Math.Max(maxLeafEntries, maxIntermediateEntries);
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.ValueScratch, Math.Max(64, valueScratchEntries * (2 + 8)));
        byte[] valueScratchArr = bufs.ValueScratch!;
        byte[] commonPrefixArr = bufs.CommonPrefixArr!;

        // CurrentLevel is pre-populated by HsstBTreeBuilder's inline-leaf emission
        // (every <c>NaiveLeafBatchSize</c> entries during Add, plus a final trigger 3
        // flush at Build start). Build() here is purely the intermediate-construction
        // loop — no leaf phase, no LeafBoundaryEnumerator, no PrecomputeCommonPrefixLengths.
        // The parallel CurrentLevelFirstKeys list carries each descriptor's first-entry
        // full key in matching order so this loop never re-reads the data section.
        ref NativeMemoryListRef<HsstIndexNodeInfo> currentNative = ref bufs.CurrentLevel;
        ref NativeMemoryListRef<HsstIndexNodeInfo> nextNative = ref bufs.NextLevel;
        ref NativeMemoryListRef<byte> currentFirstKeys = ref bufs.CurrentLevelFirstKeys;
        ref NativeMemoryListRef<byte> nextFirstKeys = ref bufs.NextLevelFirstKeys;
        nextNative.Clear();
        nextFirstKeys.Clear();

        int lastNodeLen = 0;
        int lastNodePrefixLen = 0;

        // If level 0 has a single node (one page-local leaf written by trigger 3), it
        // IS the root — return its byte length without writing any intermediate. The
        // leaf was written by HsstBTreeBuilder just before invoking us, so its bytes
        // occupy <c>[only.ChildOffset, absoluteIndexStart)</c>. The leaf descriptor
        // carries the planner-picked prefix length recorded at EmitInlineLeaf time;
        // that becomes the root's prefix length for the trailer.
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
            ref NativeMemoryListRef<HsstIndexNodeInfo> tmpNodes = ref currentNative;
            currentNative = ref nextNative;
            nextNative = ref tmpNodes;
            ref NativeMemoryListRef<byte> tmpKeys = ref currentFirstKeys;
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
    /// CurrentLevelFirstKeys / NextLevelFirstKeys in <see cref="Build"/> means at the moment
    /// this is called, <paramref name="finalLevelKeys"/> is the span of the level that holds
    /// the surviving root descriptor.
    /// </summary>
    private static void CaptureRootFirstKey(scoped ref HsstBTreeBuilderBuffers bufs, scoped ReadOnlySpan<byte> finalLevelKeys)
    {
        if (finalLevelKeys.Length == 0) return;
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.RootFirstKey, finalLevelKeys.Length);
        // finalLevelKeys.Length is one descriptor's worth of bytes (the root); copying
        // every byte is correct because RootFirstKey is sized to at least that span.
        finalLevelKeys.CopyTo(bufs.RootFirstKey);
    }

    private int _rootPrefixLen;

    /// <summary>
    /// Common-key-prefix length of the root node emitted by the last <see cref="Build"/>
    /// call. Zero for empty HSSTs. The caller writes this length into the HSST trailer.
    /// </summary>
    public int RootPrefixLen => _rootPrefixLen;

    /// <summary>
    /// Copy the root node's common-key-prefix bytes into <paramref name="dest"/>. Returns
    /// the number of bytes written (equal to <see cref="RootPrefixLen"/>). The bytes come
    /// from entry 0's key — the leftmost entry sits under every level's leftmost descendant,
    /// so its first <see cref="RootPrefixLen"/> bytes are the root's CommonKeyPrefix. By the
    /// time this is called, <see cref="Build"/> has cached the root's full first-key in
    /// <see cref="HsstBTreeBuilderBuffers.RootFirstKey"/>, so no data-section re-read is needed.
    /// </summary>
    public unsafe int CopyRootPrefixBytes(scoped Span<byte> dest)
    {
        if (_rootPrefixLen == 0) return 0;
        byte[]? rootFirstKey = Buffers.RootFirstKey;
        if (rootFirstKey is null || rootFirstKey.Length < _rootPrefixLen)
            throw new InvalidOperationException("Root first-key cache not populated by Build().");
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
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            NodeKind = BSearchNodeKind.Intermediate,
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
    /// Unified node writer: emit a <see cref="BSearchNodeKind.Intermediate"/> BSearchIndex
    /// node covering the given <paramref name="children"/>. Used for both inline page-local
    /// nodes (each child wraps a single entry; pushed from
    /// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/> trigger paths) and inner
    /// nodes (each child is a previously-emitted node). The per-child separator length is
    /// <c>max(natural LCP + 1, children[i].PrefixLen)</c>: short separators are widened so
    /// the parent's slot always carries every byte of the child's planner-picked
    /// CommonKeyPrefix. The planner then picks this node's own <c>CommonPrefixLen</c> from
    /// the shared per-entry LCP array (<paramref name="commonPrefixArr"/>) capped at
    /// <c>minLen</c> over the sepLengths. The result is returned via
    /// <paramref name="nodePrefixLen"/> so the caller can record it on the descriptor it
    /// pushes for the next level up.
    /// </summary>
    internal void WriteIndexNode(
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

        BSearchIndexLayoutPlanner.Plan(sepLengths, crossEntryLcp, _keyLength,
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

        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            NodeKind = BSearchNodeKind.Intermediate,
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
    internal int ComputeCrossEntryLcp(scoped ReadOnlySpan<HsstIndexNodeInfo> children, byte[] commonPrefixArr)
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
    /// <paramref name="key"/> starting at <paramref name="prefixLen"/>.
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
        // index 0 included). Seed sumSepBytes / maxSepLen / commonLen / firstSep
        // from that same length so the heuristic models what the writer emits —
        // for a non-first group the boundary LCP can exceed firstChild.PrefixLen.
        HsstIndexNodeInfo firstChild = level[childIdx];
        int firstNaturalSep = Math.Min(commonPrefixArr[firstChild.FirstEntry] + 1, _keyLength);
        int firstSepLen = Math.Max(firstNaturalSep, firstChild.PrefixLen);
        int childCount = 1;
        int sumSepBytes = firstSepLen;
        // Max separator length seen so far — used internally for the split heuristic
        // (forcing a split when the next child would widen the planner's Uniform key slot).
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
            int newSumSep = sumSepBytes + sepLen;
            // Phantom slot 0 restored: keys array carries newCount real separators
            // (one per child) and values array carries newCount deltas.
            int estimated = newCount * valueSlotSize + newSumSep;
            if (estimated > byteThreshold) break;

            // Dynamic split heuristics. Once minChildren is reached, break only
            // when:
            //   - effective separator (post-LCP-strip) would exceed 4 bytes —
            //     mirrors the leaf splitter's `gap > 4` rule. Combines the old
            //     "max sep widened" and "LCP shrank" checks into a single
            //     post-strip-width budget; value-slot widening is allowed.
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
            int candidateSize = IntermediateNodeSizeUpperBound(newCount, newSumSep, valueSlotSize);
            int committedSize = IntermediateNodeSizeUpperBound(childCount, sumSepBytes, committedValueSlot);
            if (childCount >= minChildren &&
                committedSize >= minBytes &&
                (newEffSepLen > 4 ||
                 WouldCrossNewPage(nodeStart, firstOffset, committedSize, candidateSize)))
                break;

            childCount = newCount;
            sumSepBytes = newSumSep;
            maxOff = newMaxOff;
            committedValueSlot = valueSlotSize;
            maxSepLen = newMaxSepLen;
            commonLen = newCommonLen;
        }
        return childCount;
    }

    // WriteInternalIndexNode and PrecomputeCommonPrefixLengths have been folded into
    // <see cref="WriteIndexNode"/> and the online LCP path in HsstBTreeBuilder.OnEntryAdded
    // respectively. Every BSearchIndex node WriteIndexNode emits has
    // <c>NodeKind=Intermediate</c>; the leaf-emission path in HsstBTreeBuilder reuses it
    // by wrapping each pending entry in a single-entry HsstIndexNodeInfo descriptor — the
    // resulting node is byte-identical to what a separate "Leaf" kind would have produced
    // and the reader recognizes its leaf-level role by peeking the leftmost child's flag
    // byte.

    /// <summary>
    /// Leaf-wide cross-entry LCP — chain-min of adjacent-key LCPs across the count entries
    /// starting at <paramref name="globalStartIndex"/>. Returns <see cref="MaxKeyLen"/> when
    /// fewer than 2 entries (no cross-entry comparison applies; planner short-circuits via minLen).
    /// </summary>
    private int ComputeCrossEntryLcpLeaf(int globalStartIndex, int count, byte[] commonPrefixArr)
    {
        if (count <= 1) return MaxKeyLen;
        int chainLcp = commonPrefixArr[globalStartIndex + 1];
        for (int j = globalStartIndex + 2; j < globalStartIndex + count; j++)
        {
            byte v = commonPrefixArr[j];
            if (v < chainLcp) chainLcp = v;
        }
        return chainLcp;
    }

    // Conservative upper bound on BSearchIndexWriter header bytes: 12 base
    // (Flags + KeyCount u16 + KeySize u16 + ValueSize u8 + BaseOffset 6) + 1
    // optional CommonPrefixLen byte + a small slack.
    private const int NodeHeaderUpperBound = 16;

    // Conservative upper bound on an intermediate node's serialised size with phantom
    // slot 0 restored: a node holding <paramref name="count"/> children emits
    // <paramref name="count"/> keys and <paramref name="count"/> values. The per-entry
    // term (2 + valueSlotSize) intentionally over-allocates by 2 bytes per value:
    // Uniform values on disk are just valueSlotSize bytes each (no length prefix),
    // but the +2 absorbs Variable-section length-table overhead and rounding slack
    // so the bound stays above the actual size for every layout the planner picks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IntermediateNodeSizeUpperBound(int count, int sumSepBytes, int valueSlotSize)
        => NodeHeaderUpperBound + sumSepBytes + count * (2 + valueSlotSize);

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


/// <summary>
/// Shared helpers for BSearchIndex value-slot encoding.
///
/// The BSearchIndex header packs the value-slot width into 2 bits of the Flags byte
/// (bits 3-4), so the format only encodes the four widths <c>{2, 3, 4, 6}</c>. The
/// <see cref="MinBytesFor"/> helper rounds an arbitrary natural width up to the next
/// supported value. Lives in its own non-generic class so the leaf-boundary
/// enumerator (which sits outside <see cref="HsstIndexBuilder{TWriter,TReader,TPin}"/>'s
/// generic instantiation) can call it without specifying type arguments.
/// </summary>
internal static class HsstValueSlot
{
    /// <summary>
    /// Smallest supported value-slot width that can encode <paramref name="value"/>:
    /// returns 2 for 0/1/2-byte naturals, 3 for 3, 4 for 4, and 6 for 5/6. Naturals
    /// larger than 6 bytes never occur in practice because <c>BaseOffset</c> already
    /// caps the encodable delta range at 2⁴⁸ − 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MinBytesFor(long value)
    {
        int natural = value == 0 ? 1 : (BitOperations.Log2((ulong)value) >> 3) + 1;
        return natural <= 2 ? 2
            : natural == 3 ? 3
            : natural == 4 ? 4
            : 6; // 5 and 6 both pad up to 6
    }
}
