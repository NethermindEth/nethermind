// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds the B-tree index region for an HSST block.
/// Takes (entryPositions, dataSectionReader) and produces a complete index region
/// where the root index is the last block (readable from end via MetadataLength byte).
///
/// Per-key state during this build phase is one <c>long</c> position; full keys are
/// recovered on demand by reading them back from the data section through the
/// supplied reader. Per-entry common prefix lengths against the prior entry's key are
/// precomputed once into <see cref="_commonPrefixArr"/> by
/// <see cref="PrecomputeCommonPrefixLengths"/>; leaf separators are derived as
/// <c>min(commonPrefix + 1, currKeyLen)</c>. Internal-node separators are produced
/// via <see cref="WriteSeparatorBetween"/> over the two boundary keys.
/// </summary>
public ref struct HsstIndexBuilder<TWriter, TReader, TPin>
    where TWriter : IByteBufferWriterWithReader<TReader, TPin>
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    private const int MaxKeyLen = 255;

    private ref TWriter _writer;
    private TReader _reader;
    private readonly ReadOnlySpan<long> _entryPositions;
    // Fixed key length for every entry (HsstBTreeBuilder enforces uniformity). Used directly
    // wherever we previously called ReadKeyLength / tracked minKeyLen — those collapse to
    // this single scalar.
    private readonly int _keyLength;
    // One byte per entry: LCP(prev_i, curr_i) — the common prefix length of each entry's
    // key against the prior entry's key. Filled once by PrecomputeCommonPrefixLengths at
    // Build() entry; PlanLeafBoundaries / WriteLeafIndexNode derive the natural separator
    // length on demand as min(commonPrefix + 1, _keyLength). Rented from ArrayPool;
    // returned in Build's finally.
    private byte[]? _commonPrefixArr;

    // Iterative min-segment tree over _commonPrefixArr. Leaves live at [base..base+n-1];
    // internal nodes at [1..base-1]. Sentinel byte.MaxValue fills the tail past entry n.
    // Used by the top-down leaf splitter to query the minimum LCP across an entry range
    // in O(log n) — far cheaper than scanning when the same range is queried at multiple
    // recursion depths. Rented from ArrayPool; returned in Build's finally.
    private byte[]? _segTree;
    private int _segTreeBase;

    public HsstIndexBuilder(ref TWriter writer, TReader reader, ReadOnlySpan<long> entryPositions, int keyLength)
    {
        _writer = ref writer;
        _reader = reader;
        _entryPositions = entryPositions;
        _keyLength = keyLength;
    }

    /// <summary>
    /// Build B-tree index via writer.
    /// The absolute data region start offset (= 1 + dataLen) is needed to compute child offsets.
    /// Returns the byte length of the root node — the caller writes a u16 trailer with that
    /// value so readers can locate the root from the HSST end.
    /// </summary>
    public int Build(long absoluteIndexStart,
        int maxLeafEntries = HsstBTreeOptions.DefaultMaxLeafEntries,
        int maxIntermediateEntries = HsstBTreeOptions.DefaultMaxIntermediateEntries,
        int minLeafEntries = HsstBTreeOptions.DefaultMinLeafEntries,
        int maxIntermediateBytes = HsstBTreeOptions.DefaultMaxIntermediateBytes,
        int minIntermediateChildren = HsstBTreeOptions.DefaultMinIntermediateChildren,
        int minIntermediateBytes = HsstBTreeOptions.DefaultMinIntermediateBytes)
    {
        long startWritten = _writer.Written;
        long firstOffset = _writer.FirstOffset;

        if (_entryPositions.Length == 0)
        {
            // Empty index: write a single empty leaf node.
            return WriteEmptyLeafIndexNode();
        }

        if (minLeafEntries > maxLeafEntries) minLeafEntries = maxLeafEntries;
        if (minLeafEntries < 1) minLeafEntries = 1;
        if (minIntermediateChildren > maxIntermediateEntries) minIntermediateChildren = maxIntermediateEntries;
        if (minIntermediateChildren < 1) minIntermediateChildren = 1;
        if (minIntermediateBytes < 0) minIntermediateBytes = 0;
        if (minIntermediateBytes > maxIntermediateBytes) minIntermediateBytes = maxIntermediateBytes;

        int n = _entryPositions.Length;

        // Reusable per-node value scratch. Each entry's value slot is at most 8 bytes
        // (Uniform offset width) plus a 2-byte u16 length prefix in the writer's buffer.
        // Sized for the larger of leaf/intermediate fan-out.
        int valueScratchEntries = Math.Max(maxLeafEntries, maxIntermediateEntries);
        byte[] valueScratchArr = ArrayPool<byte>.Shared.Rent(Math.Max(64, valueScratchEntries * (2 + 8)));

        _commonPrefixArr = ArrayPool<byte>.Shared.Rent(n);

        // Segment-tree base: smallest power-of-two ≥ n.
        int segBase = 1;
        while (segBase < n) segBase <<= 1;
        _segTreeBase = segBase;
        _segTree = ArrayPool<byte>.Shared.Rent(segBase * 2);

        // Planning scratch: leafCounts records one count per emitted leaf in sorted
        // order; rangeStack drives the iterative DFS. Worst case both are bounded by
        // n / 2*n respectively (every entry its own leaf under uniform-LCP forced
        // splits). The stack stores (lo, hi) pairs so peak depth × branching is
        // bounded by 2n.
        int[] leafCountsArr = ArrayPool<int>.Shared.Rent(Math.Max(1, n));
        int[] rangeStackArr = ArrayPool<int>.Shared.Rent(Math.Max(4, 2 * n));

        const int StackThreshold = 1024;
        NativeMemoryListRef<NodeInfo> currentNative = default;
        NativeMemoryListRef<NodeInfo> nextNative = default;
        scoped Span<NodeInfo> currentLevel = default;
        scoped Span<NodeInfo> nextLevel = default;

        // lastNodeLen tracks the byte length of the most recently written node; the
        // returned value is the root node's size (the last node emitted).
        int lastNodeLen = 0;

        try
        {
            PrecomputeCommonPrefixLengths();
            BuildLcpSegTree();

            // Plan all leaf boundaries up-front with a top-down splitter so leaf
            // sizing reflects the global LCP picture, not a left-to-right greedy
            // accumulation. The planner returns the exact leaf count, which sizes
            // the level buffers tightly below.
            int leafCount = PlanLeafBoundaries(leafCountsArr, rangeStackArr, minLeafEntries, maxLeafEntries);

            if (leafCount <= StackThreshold)
            {
                currentLevel = stackalloc NodeInfo[leafCount];
                nextLevel = stackalloc NodeInfo[leafCount];
            }
            else
            {
                currentNative = new NativeMemoryListRef<NodeInfo>(leafCount, leafCount);
                nextNative = new NativeMemoryListRef<NodeInfo>(leafCount, leafCount);
                currentLevel = currentNative.AsSpan();
                nextLevel = nextNative.AsSpan();
            }

            int currentLevelCount = 0;
            int entryIdx = 0;

            // True until the first node of the index region has been written.
            // Used to gate MaybePadToNextPage so we never pad after the root —
            // the trailer formula assumes [...root...][trailer] with no gap.
            bool firstNode = true;

            for (int li = 0; li < leafCount; li++)
            {
                int count = leafCountsArr[li];

                // Pad to a fresh page if we're within PageAlignPadThreshold of
                // the boundary. Skipped on the first node — there's nothing to
                // pad away from yet.
                if (!firstNode) MaybePadToNextPage();
                firstNode = false;

                long nodeStart = _writer.Written;
                long relativeStart = nodeStart - startWritten;
                WriteLeafIndexNode(
                    entryIdx, count,
                    valueScratchArr);
                int nodeLen = checked((int)(_writer.Written - nodeStart));
                lastNodeLen = nodeLen;

                // childOffset = absolute first byte position of this node.
                long childOffset = absoluteIndexStart + relativeStart;

                currentLevel[currentLevelCount++] = new NodeInfo(
                    childOffset,
                    entryIdx,
                    entryIdx + count - 1);

                entryIdx += count;
            }

            // Build internal levels until single root
            while (currentLevelCount > 1)
            {
                int nextLevelCount = 0;
                int childIdx = 0;

                while (childIdx < currentLevelCount)
                {
                    int childCount = ChooseIntermediateChildCount(
                        currentLevel[..currentLevelCount], childIdx,
                        maxIntermediateEntries, maxIntermediateBytes,
                        minIntermediateChildren, minIntermediateBytes,
                        _writer.Written, firstOffset,
                        out int crossEntryLcp);
                    ReadOnlySpan<NodeInfo> children = currentLevel.Slice(childIdx, childCount);

                    // Always non-first here (at least one leaf already written).
                    MaybePadToNextPage();

                    long nodeStart = _writer.Written;
                    long relativeStart = nodeStart - startWritten;
                    WriteInternalIndexNode(children, crossEntryLcp, valueScratchArr);
                    int nodeLen = checked((int)(_writer.Written - nodeStart));
                    lastNodeLen = nodeLen;

                    NodeInfo first = children[0];
                    NodeInfo last = children[childCount - 1];

                    long childOffset = absoluteIndexStart + relativeStart;

                    nextLevel[nextLevelCount++] = new NodeInfo(
                        childOffset,
                        first.FirstEntry,
                        last.LastEntry);

                    childIdx += childCount;
                }

                nextLevel[..nextLevelCount].CopyTo(currentLevel);
                currentLevelCount = nextLevelCount;
            }
        }
        finally
        {
            currentNative.Dispose();
            nextNative.Dispose();
            ArrayPool<byte>.Shared.Return(valueScratchArr);
            ArrayPool<byte>.Shared.Return(_commonPrefixArr);
            _commonPrefixArr = null;
            ArrayPool<byte>.Shared.Return(_segTree);
            _segTree = null;
            ArrayPool<int>.Shared.Return(leafCountsArr);
            ArrayPool<int>.Shared.Return(rangeStackArr);
        }

        return lastNodeLen;
    }

    /// <summary>
    /// One-time fill of <see cref="_segTree"/> as an iterative min-segment tree over
    /// <see cref="_commonPrefixArr"/>. Leaves live at <c>[segBase, segBase+n)</c>; the
    /// tail <c>[segBase+n, 2*segBase)</c> is padded with <see cref="byte.MaxValue"/> so
    /// queries past the last entry don't pull the min down. Built bottom-up so the run
    /// is a single contiguous sweep over the rented buffer.
    /// </summary>
    private void BuildLcpSegTree()
    {
        int n = _entryPositions.Length;
        int b = _segTreeBase;
        byte[] tree = _segTree!;
        byte[] src = _commonPrefixArr!;
        for (int i = 0; i < n; i++) tree[b + i] = src[i];
        for (int i = b + n; i < b * 2; i++) tree[i] = byte.MaxValue;
        for (int i = b - 1; i >= 1; i--)
        {
            byte a = tree[i * 2];
            byte c = tree[i * 2 + 1];
            tree[i] = a < c ? a : c;
        }
    }

    /// <summary>
    /// Min over <see cref="_commonPrefixArr"/> in the inclusive range <c>[l, r]</c>,
    /// answered via <see cref="_segTree"/> in O(log n). Iterative bottom-up walk: at each
    /// level absorb the left fringe when <c>l</c> is a right child, absorb the right
    /// fringe when <c>r</c> is a left child, then ascend. Caller is responsible for
    /// ensuring <c>l ≤ r</c>; an out-of-range query returns <see cref="byte.MaxValue"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RangeMinLcp(int l, int r)
    {
        byte[] tree = _segTree!;
        int b = _segTreeBase;
        l += b;
        r += b;
        int res = byte.MaxValue;
        while (l <= r)
        {
            if ((l & 1) == 1) { int v = tree[l]; if (v < res) res = v; l++; }
            if ((r & 1) == 0) { int v = tree[r]; if (v < res) res = v; r--; }
            l >>= 1;
            r >>= 1;
        }
        return res;
    }

    /// <summary>
    /// Top-down leaf splitter. Recursively (via an iterative DFS stack) partitions the
    /// entry range <c>[0, n-1]</c> with a single pivot per level — the rightmost position
    /// in the first half whose adjacent-key LCP equals the range minimum (the
    /// "highest-positioned minimum-pivot before halfpoint"), with a leftmost-in-second-half
    /// fallback. Writes resulting leaf sizes into <paramref name="leafCounts"/> in sorted
    /// order and returns the count.
    ///
    /// Per-range decision:
    /// <list type="bullet">
    /// <item><description><c>count ≤ minLeafEntries</c> — base case, emit as a single
    /// leaf.</description></item>
    /// <item><description><c>count &gt; maxLeafEntries</c> — forced split (hard cap on
    /// leaf entry count).</description></item>
    /// <item><description>Otherwise — encoding-quality gate. The range emits as a single
    /// leaf only when the BSearchIndex layout will be cheap to evaluate. Three gates
    /// force a split:
    ///   <list type="bullet">
    ///   <item><description><c>maxLcp − minLcp &gt; 4</c> — post-strip separator slot
    ///   exceeds the 4-byte SIMD ceiling, forcing the planner to Variable
    ///   encoding.</description></item>
    ///   <item><description><c>maxLcp − minLcp == 3</c> — slot width 3 is the only ≤4
    ///   width that isn't power-of-two-friendly on the SIMD paths.</description></item>
    ///   <item><description><c>maxVal − minVal &gt; 2²⁴</c> — value slot widens past 3
    ///   bytes; splitting almost always recovers a 3-byte slot because entries inside a
    ///   leaf land in a tight stretch of the data section.</description></item>
    ///   </list>
    /// </description></item>
    /// </list>
    ///
    /// A single pass over <c>[lo, hi]</c> computes <c>maxLcp</c>, the pivot positions, and
    /// the value range. <c>minLcp</c> comes from <see cref="RangeMinLcp"/> up front. The
    /// right half is pushed before the left so the DFS pops them left-to-right.
    /// </summary>
    private int PlanLeafBoundaries(int[] leafCounts, int[] rangeStack, int minLeafEntries, int maxLeafEntries)
    {
        int n = _entryPositions.Length;
        int leafCount = 0;
        int sp = 0;
        rangeStack[sp++] = 0;
        rangeStack[sp++] = n - 1;

        byte[] lcp = _commonPrefixArr!;
        ReadOnlySpan<long> entryPos = _entryPositions;
        const long ValueRangeLimit = 1L << 24;

        while (sp > 0)
        {
            int hi = rangeStack[--sp];
            int lo = rangeStack[--sp];
            int count = hi - lo + 1;

            if (count <= minLeafEntries)
            {
                leafCounts[leafCount++] = count;
                continue;
            }

            int minLcp = RangeMinLcp(lo + 1, hi);

            // Halfpoint is the last LCP index in the "first half". Splitting at k creates
            // [lo, k-1] (size k - lo) and [k, hi] (size hi - k + 1); a pivot at k = lo + count/2
            // yields halves of size count/2 and ⌈count/2⌉.
            int half = lo + (count >> 1);

            int pivotFirst = -1;
            int pivotSecond = -1;

            if (count <= maxLeafEntries)
            {
                // Quality-gate path. Single pass over [lo, hi] tracks max LCP, the two
                // pivot candidates (rightmost min in [lo+1, half], leftmost min in
                // (half, hi]), and min / max of _entryPositions for the value-range gate.
                // Position lo only feeds the value-range trackers — its LCP is the
                // "no previous key" sentinel.
                int maxLcp = 0;
                long minVal = entryPos[lo];
                long maxVal = minVal;
                for (int k = lo + 1; k <= hi; k++)
                {
                    int v = lcp[k];
                    if (v > maxLcp) maxLcp = v;
                    if (v == minLcp)
                    {
                        if (k <= half) pivotFirst = k;
                        else if (pivotSecond < 0) pivotSecond = k;
                    }
                    long ep = entryPos[k];
                    if (ep < minVal) minVal = ep;
                    if (ep > maxVal) maxVal = ep;
                }

                int gap = maxLcp - minLcp;
                bool splitNeeded = gap > 4 || gap == 3 || (maxVal - minVal) > ValueRangeLimit;
                if (!splitNeeded)
                {
                    leafCounts[leafCount++] = count;
                    continue;
                }
            }
            else
            {
                // Forced split — the quality gate result is unused; skip the maxLcp /
                // value-range tracking and scan only for the pivot. This is the hot path
                // for ranges above maxLeafEntries; doing the full pass would be wasteful.
                for (int k = lo + 1; k <= hi; k++)
                {
                    if (lcp[k] == minLcp)
                    {
                        if (k <= half) pivotFirst = k;
                        else if (pivotSecond < 0) { pivotSecond = k; break; }
                    }
                }
            }

            int split = pivotFirst >= 0 ? pivotFirst : pivotSecond;

            // Push right half first, left half second, so the DFS processes left first.
            rangeStack[sp++] = split;
            rangeStack[sp++] = hi;
            rangeStack[sp++] = lo;
            rangeStack[sp++] = split - 1;
        }

        return leafCount;
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

    private int WriteEmptyLeafIndexNode()
    {
        long nodeStart = _writer.Written;
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = false,
            KeyType = 0,
            BaseOffset = 0,
            KeySlotSize = 1,
            ValueType = 1,
            ValueSlotSize = 1,
        }, default, default);
        indexWriter.FinalizeNode();
        return checked((int)(_writer.Written - nodeStart));
    }

    private void WriteLeafIndexNode(
        int globalStartIndex, int count,
        scoped Span<byte> valueScratch)
    {
        // Per-entry natural separator length, capped at _keyLength: min(LCP(prev,curr)+1, key).
        // Widening to slot=4 (when applicable) is the planner's call now.
        Span<int> sepLengths = stackalloc int[count];
        for (int i = 0; i < count; i++)
            sepLengths[i] = Math.Min(_commonPrefixArr![globalStartIndex + i] + 1, _keyLength);

        // Metadata-start range for value-slot sizing — key lengths are uniform, no per-entry reads.
        Span<long> metadataStarts = stackalloc long[count];
        long minVal = long.MaxValue, maxVal = 0;
        for (int i = 0; i < count; i++)
        {
            long md = _entryPositions[globalStartIndex + i];
            metadataStarts[i] = md;
            if (md < minVal) minVal = md;
            if (md > maxVal) maxVal = md;
        }

        long baseOffset = 0;
        if (count > 1 && minVal > 0 && minVal < maxVal) baseOffset = minVal;
        int valueSlotSize = MinBytesFor(maxVal - baseOffset);

        int crossEntryLcp = ComputeCrossEntryLcpLeaf(globalStartIndex, count);
        BSearchIndexLayoutPlanner.Plan(sepLengths, crossEntryLcp, _keyLength,
            out int prefixLen, out int keyType, out int keySlotSize, out bool keyLittleEndian);

        // Pass 2: ReadKey + AddKey. Entry 0's ReadKey also feeds commonPrefix. The planner's
        // keySlotSize (post-widen, post-strip) drives slice width — may exceed sepLengths[i]
        // when the planner widened, in which case we read more bytes from the key.
        Span<byte> currKey = stackalloc byte[MaxKeyLen];
        Span<byte> commonPrefixBuf = stackalloc byte[prefixLen];

        // keyBuf must fit the widest per-entry payload across layouts: Uniform takes
        // keySlotSize bytes, Variable/UniformWithLen take the per-entry natural sep
        // length (up to _keyLength - prefixLen). Use the max so all paths fit.
        int perEntryKeyBytes = Math.Max(keySlotSize, _keyLength - prefixLen);
        int keyBufSize = count * (2 + perEntryKeyBytes);
        Span<byte> keyBuf = stackalloc byte[keyBufSize];
        Span<byte> valueScratchSlice = valueScratch[..(count * (2 + valueSlotSize))];

        ReadKey(globalStartIndex, currKey);
        currKey[..prefixLen].CopyTo(commonPrefixBuf);

        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = false,
            KeyType = keyType,
            BaseOffset = (ulong)baseOffset,
            KeySlotSize = keySlotSize,
            ValueType = 1,
            ValueSlotSize = valueSlotSize,
            IsKeyLittleEndian = keyLittleEndian,
        }, keyBuf, valueScratchSlice, commonPrefixBuf);

        Span<byte> valueBuf = stackalloc byte[8];

        // Entry 0: already in currKey.
        WriteUInt64LE(valueBuf, metadataStarts[0] - baseOffset, valueSlotSize);
        indexWriter.AddKey(currKey.Slice(prefixLen, KeySliceLength(prefixLen, keyType, keySlotSize, sepLengths[0])), valueBuf[..valueSlotSize]);

        for (int i = 1; i < count; i++)
        {
            ReadKey(globalStartIndex + i, currKey);
            WriteUInt64LE(valueBuf, metadataStarts[i] - baseOffset, valueSlotSize);
            indexWriter.AddKey(currKey.Slice(prefixLen, KeySliceLength(prefixLen, keyType, keySlotSize, sepLengths[i])), valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
    }

    /// <summary>
    /// Slice the per-entry key bytes for the writer based on layout:
    /// Uniform (keyType=1) takes a fixed <paramref name="keySlotSize"/> bytes;
    /// Variable (0) and UniformWithLen (2) take the entry's natural sep length
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
        scoped ReadOnlySpan<NodeInfo> level, int childIdx,
        int maxChildren, int byteThreshold,
        int minChildren, int minBytes,
        long nodeStart, long firstOffset,
        out int crossEntryLcp)
    {
        // Running chain-min over _commonPrefixArr covering the range between the first
        // sep's right-key and the latest committed sep's right-key. Surfaced so the
        // planner can derive the leaf-wide common prefix without scanning sep bytes.
        // Upper-bound init: planner caps via min(minLen, crossEntryLcp).
        crossEntryLcp = MaxKeyLen;
        int remaining = level.Length - childIdx;
        int hardMax = Math.Min(maxChildren, remaining);
        if (hardMax <= 1) return hardMax;

        int childCount = 1;
        int sumSepBytes = 0;
        // Max separator length seen so far — used internally for the split heuristic
        // (forcing a split when the next child would widen the planner's Uniform key slot).
        int maxSepLen = 0;
        // BaseOffset is fixed at the leftmost child's absolute offset; remaining
        // children encode as deltas. valueSlotSize tracks the min byte width for
        // the current max delta over children[1..].
        long baseChildOffset = level[childIdx].ChildOffset;
        long maxOff = baseChildOffset;
        int committedValueSlot = MinBytesFor(0);
        // Common-prefix length across separators observed so far. Sentinel -1
        // means "no separator seen yet" (childCount == 1, no firstSep). On the
        // first separator we seed commonLen = sepLen and copy the bytes into
        // firstSep; subsequent separators shrink commonLen via LCP.
        int commonLen = -1;
        Span<byte> firstSep = stackalloc byte[MaxKeyLen];

        Span<byte> leftKey = stackalloc byte[MaxKeyLen];
        Span<byte> rightKey = stackalloc byte[MaxKeyLen];
        Span<byte> sepBuf = stackalloc byte[MaxKeyLen];

        while (childCount < hardMax)
        {
            NodeInfo prev = level[childIdx + childCount - 1];
            NodeInfo curr = level[childIdx + childCount];
            int leftLen = ReadKey(prev.LastEntry, leftKey);
            int rightLen = ReadKey(curr.FirstEntry, rightKey);
            int sepLen = WriteSeparatorBetween(sepBuf, leftKey[..leftLen], rightKey[..rightLen]);

            long newMaxOff = curr.ChildOffset > maxOff ? curr.ChildOffset : maxOff;
            int valueSlotSize = MinBytesFor(newMaxOff - baseChildOffset);
            int newMaxSepLen = sepLen > maxSepLen ? sepLen : maxSepLen;

            int newCommonLen;
            if (commonLen < 0)
            {
                // First separator → seeds the common prefix.
                newCommonLen = sepLen;
            }
            else
            {
                int boundary = Math.Min(commonLen, sepLen);
                newCommonLen = commonLen == 0
                    ? 0
                    : CommonPrefixLength(firstSep[..boundary], sepBuf[..boundary]);
            }

            int newCount = childCount + 1;
            int newSumSep = sumSepBytes + sepLen;
            // Phantom slot 0 dropped: keys array carries newCount-1 real
            // separators and values array carries newCount-1 deltas.
            int estimated = (newCount - 1) * valueSlotSize + newSumSep;
            if (estimated > byteThreshold) break;

            // Dynamic split heuristics, mirrors ChooseLeafLayout. Once
            // minChildren reached, break early when adding the next child would
            // worsen the per-node encoding even if it still fits the byte
            // budget:
            //   - newMaxSepLen > maxSepLen: widens the planner's Uniform key slot
            //     (or forces Variable layout), enlarging every per-entry slot.
            //   - newCommonLen < commonLen (after the first sep is seen):
            //     planner strips fewer bytes per slot, fattening every entry.
            //   - valueSlotSize > committedValueSlot: child-offset range widened,
            //     bumping every Uniform value slot to a wider encoding.
            //   - WouldCrossNewPage: candidate node would straddle a 4 KiB page
            //     boundary the committed node does not.
            int candidateSize = IntermediateNodeSizeUpperBound(newCount, newSumSep, valueSlotSize);
            int committedSize = IntermediateNodeSizeUpperBound(childCount, sumSepBytes, committedValueSlot);
            if (childCount >= minChildren &&
                committedSize >= minBytes &&
                (newMaxSepLen > maxSepLen ||
                 (commonLen >= 0 && newCommonLen < commonLen) ||
                 valueSlotSize > committedValueSlot ||
                 WouldCrossNewPage(nodeStart, firstOffset, committedSize, candidateSize)))
                break;

            // Absorb _commonPrefixArr range [prevRight+1, currRight] into crossEntryLcp once
            // we have at least two committed seps to compare. childCount here is the count
            // BEFORE this child commits — so childCount >= 2 means a prior sep exists.
            if (childCount >= 2)
            {
                int prevRight = level[childIdx + childCount - 1].FirstEntry;
                int currRight = curr.FirstEntry;
                for (int j = prevRight + 1; j <= currRight; j++)
                {
                    byte v = _commonPrefixArr![j];
                    if (v < crossEntryLcp) crossEntryLcp = v;
                }
            }

            childCount = newCount;
            sumSepBytes = newSumSep;
            maxOff = newMaxOff;
            committedValueSlot = valueSlotSize;
            maxSepLen = newMaxSepLen;
            if (commonLen < 0)
            {
                sepBuf[..sepLen].CopyTo(firstSep);
            }
            commonLen = newCommonLen;
        }
        return childCount;
    }

    private void WriteInternalIndexNode(
        scoped ReadOnlySpan<NodeInfo> children,
        int crossEntryLcp,
        scoped Span<byte> valueScratch)
    {
        int childCount = children.Length;
        // Phantom slot 0 dropped: for N children, the keys array carries the
        // N-1 real separators between adjacent children, and the values array
        // carries N-1 deltas for children[1..]. BaseOffset names the leftmost
        // child's absolute offset directly; the reader's no-floor fallback
        // routes k < smallest-separator queries to it. For a 1-child node
        // (entryCount == 0) the reader recovers the lone child purely via
        // BaseOffset.
        int entryCount = childCount > 0 ? childCount - 1 : 0;

        // Per-sep natural separator length: each sep disambiguates two adjacent leaf-entry
        // keys (left = curr.FirstEntry-1, right = curr.FirstEntry). LCP comes straight from
        // the cache. Widening is the planner's call.
        Span<int> sepLengths = stackalloc int[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            int rightIdx = children[i + 1].FirstEntry;
            sepLengths[i] = Math.Min(_commonPrefixArr![rightIdx] + 1, _keyLength);
        }

        BSearchIndexLayoutPlanner.Plan(sepLengths, crossEntryLcp, _keyLength,
            out int prefixLen, out int keyType, out int keySlotSize, out bool keyLittleEndian);

        // BaseOffset is the leftmost child's absolute offset (always — no
        // longer the conditional min selection of the phantom-slot layout).
        // valueSlotSize is the min byte width that fits the largest delta
        // over children[1..].
        long baseOffset = children[0].ChildOffset;
        long maxVal = baseOffset;
        for (int i = 1; i < childCount; i++)
        {
            if (children[i].ChildOffset > maxVal) maxVal = children[i].ChildOffset;
        }
        int valueSlotSize = MinBytesFor(maxVal - baseOffset);

        // Pass 2: ReadKey rightKey + AddKey. Sep 0's rightKey also feeds commonPrefix.
        // The planner's keySlotSize (post-widen, post-strip) drives slice width.
        Span<byte> rightKey = stackalloc byte[MaxKeyLen];
        Span<byte> commonPrefixBuf = stackalloc byte[prefixLen];

        // keyBuf must fit the widest per-entry payload across layouts (see WriteLeafIndexNode).
        int perEntryKeyBytes = entryCount > 0 ? Math.Max(keySlotSize, _keyLength - prefixLen) : 0;
        int keyBufSize = entryCount * (2 + perEntryKeyBytes);
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        Span<byte> valueScratchSlice = valueScratch[..(entryCount * (2 + valueSlotSize))];

        if (entryCount > 0)
        {
            ReadKey(children[1].FirstEntry, rightKey);
            rightKey[..prefixLen].CopyTo(commonPrefixBuf);
        }

        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = true,
            KeyType = keyType,
            BaseOffset = (ulong)baseOffset,
            KeySlotSize = keySlotSize,
            ValueType = 1,
            ValueSlotSize = valueSlotSize,
            IsKeyLittleEndian = keyLittleEndian,
        }, keyBuf, valueScratchSlice, commonPrefixBuf);

        Span<byte> valueBuf = stackalloc byte[8];

        if (entryCount > 0)
        {
            WriteUInt64LE(valueBuf, children[1].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(rightKey.Slice(prefixLen, KeySliceLength(prefixLen, keyType, keySlotSize, sepLengths[0])), valueBuf[..valueSlotSize]);
        }
        for (int i = 1; i < entryCount; i++)
        {
            ReadKey(children[i + 1].FirstEntry, rightKey);
            WriteUInt64LE(valueBuf, children[i + 1].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(rightKey.Slice(prefixLen, KeySliceLength(prefixLen, keyType, keySlotSize, sepLengths[i])), valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
    }

    /// <summary>
    /// One-pass pre-computation of per-entry <c>LCP(prev, curr)</c> — the common prefix
    /// length of each entry's key against the prior entry's key. Writes into
    /// <see cref="_commonPrefixArr"/> (one byte per entry — fits because LCP is bounded
    /// by min(prev.Length, curr.Length) ≤ <see cref="MaxKeyLen"/> = 255). Consumers
    /// derive the natural separator length as <c>min(cp + 1, currKeyLen)</c>.
    /// </summary>
    private void PrecomputeCommonPrefixLengths()
    {
        int n = _entryPositions.Length;
        Span<byte> prevKey = stackalloc byte[MaxKeyLen];
        Span<byte> currKey = stackalloc byte[MaxKeyLen];
        int prevKeyLen = 0;
        for (int i = 0; i < n; i++)
        {
            int currKeyLen = ReadKey(i, currKey);
            int cp = CommonPrefixLength(prevKey[..prevKeyLen], currKey[..currKeyLen]);
            _commonPrefixArr![i] = (byte)cp;
            currKey[..currKeyLen].CopyTo(prevKey);
            prevKeyLen = currKeyLen;
        }
    }

    /// <summary>
    /// Read the full key for entry index <paramref name="idx"/> into <paramref name="dest"/>.
    /// Walks the LEB128 ValueLength header byte-by-byte (so end-of-data-section reads
    /// stay in bounds), then reads the KeyLength byte and the key bytes.
    /// Returns the key length (≤ 255).
    /// </summary>
    private int ReadKey(int idx, scoped Span<byte> dest)
    {
        long pos = _entryPositions[idx];
        Span<byte> oneByte = stackalloc byte[1];

        // Skip LEB128 ValueLength.
        long offset = pos;
        do
        {
            if (!_reader.TryRead(offset, oneByte)) ThrowReadFailed();
            offset++;
        } while ((oneByte[0] & 0x80) != 0);

        // KeyLength byte.
        if (!_reader.TryRead(offset, oneByte)) ThrowReadFailed();
        int keyLen = oneByte[0];
        offset++;

        if (keyLen > 0)
        {
            if (!_reader.TryRead(offset, dest[..keyLen])) ThrowReadFailed();
        }
        return keyLen;
    }

    /// <summary>
    /// Leaf-wide cross-entry LCP — chain-min of adjacent-key LCPs across the count entries
    /// starting at <paramref name="globalStartIndex"/>. Returns <see cref="MaxKeyLen"/> when
    /// fewer than 2 entries (no cross-entry comparison applies; planner short-circuits via minLen).
    /// </summary>
    private int ComputeCrossEntryLcpLeaf(int globalStartIndex, int count)
    {
        if (count <= 1) return MaxKeyLen;
        int chainLcp = _commonPrefixArr![globalStartIndex + 1];
        for (int j = globalStartIndex + 2; j < globalStartIndex + count; j++)
        {
            byte v = _commonPrefixArr![j];
            if (v < chainLcp) chainLcp = v;
        }
        return chainLcp;
    }

    private static void ThrowReadFailed()
        => throw new IOException("HSST data-section read out of range during index build.");

    // Conservative upper bound on BSearchIndexWriter header bytes: 12 base
    // (Flags + KeyCount u16 + KeySize u16 + ValueSize u8 + BaseOffset 6) + 1
    // optional CommonPrefixLen byte + a small slack.
    private const int NodeHeaderUpperBound = 16;

    // Conservative upper bound on an intermediate node's serialised size. The
    // phantom leftmost slot is dropped, so a node holding <paramref name="count"/>
    // children emits count-1 keys and count-1 values. Keys are variable-length;
    // include the 2-byte u16 length prefix that BSearchIndexWriter accumulates
    // per key (matches WriteInternalIndexNode's keyBufSize before plan-time
    // prefix stripping).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IntermediateNodeSizeUpperBound(int count, int sumSepBytes, int valueSlotSize)
        => NodeHeaderUpperBound + sumSepBytes + (count > 0 ? count - 1 : 0) * (2 + valueSlotSize);

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
        long pageOff = (nodeStart - firstOffset) & 4095L;
        bool committedCrosses = pageOff + committedSize > 4096;
        bool candidateCrosses = pageOff + candidateSize > 4096;
        return candidateCrosses && !committedCrosses;
    }

    /// <summary>
    /// Bytes-to-next-page threshold below which the builder pads up to the page
    /// boundary before writing the next node. Companion to <see cref="WouldCrossNewPage"/>:
    /// the page-crossing heuristic stops a node growing into the next page, but
    /// the next node would then start at the seam and be guaranteed to cross.
    /// Padding eats the small leftover (≤<see cref="PageAlignPadThreshold"/> bytes)
    /// so the next node opens on a fresh page. Threshold is intentionally large
    /// so most splits earn the alignment; nodes finalised well inside their page
    /// (gap > threshold) skip padding to avoid writing kilobytes of zeros.
    /// </summary>
    private const int PageAlignPadThreshold = 64;

    /// <summary>
    /// If the writer is within <see cref="PageAlignPadThreshold"/> bytes of the
    /// next 4 KiB boundary, pad up to that boundary so the next node starts on a
    /// fresh page. Padding bytes are inert: parent nodes record exact child
    /// offsets, so readers never look at the padding region. Caller must avoid
    /// invoking this after the very last node (root) — the trailer formula
    /// <c>root_start = HSST_end - 3 - rootSize</c> assumes the trailer abuts the
    /// root, and any padding between them would offset the computed root start.
    /// </summary>
    private void MaybePadToNextPage()
    {
        long firstOffset = _writer.FirstOffset;
        long pageOff = (_writer.Written - firstOffset) & 4095L;
        if (pageOff == 0) return;
        long remaining = 4096L - pageOff;
        if (remaining > PageAlignPadThreshold) return;
        int len = (int)remaining;
        Span<byte> pad = _writer.GetSpan(len);
        pad[..len].Clear();
        _writer.Advance(len);
    }

    /// <summary>
    /// Smallest 1..8 byte width that can encode <paramref name="value"/>. Returns 1 for 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinBytesFor(long value)
    {
        if (value == 0) return 1;
        return (BitOperations.Log2((ulong)value) >> 3) + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64LE(Span<byte> dest, long value, int width)
    {
        for (int i = 0; i < width; i++)
            dest[i] = (byte)(value >> (i * 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int WriteSeparatorBetween(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int minSeparatorLength = 0)
    {
        int minLen = Math.Min(left.Length, right.Length);
        int len = right.Length;
        for (int i = 0; i < minLen; i++)
        {
            if (left[i] != right[i])
            {
                len = i + 1;
                break;
            }
        }
        // Apply minSeparatorLength floor (clamped to right.Length) so internal-node
        // separators stay uniform when the caller has signalled a fixed key width.
        // Extending the prefix further (still a prefix of right) preserves the
        // invariants: the result is > left and ≤ right.
        if (minSeparatorLength > len)
            len = Math.Min(minSeparatorLength, right.Length);
        right[..len].CopyTo(output);
        return len;
    }

    internal readonly struct NodeInfo(long childOffset, int firstEntry, int lastEntry)
    {
        /// <summary>Absolute first-byte position of this node in _data (= absoluteIndexStart + relativeStart).</summary>
        public readonly long ChildOffset = childOffset;
        /// <summary>Index (into <c>_entryPositions</c>) of the first leaf entry under this subtree.</summary>
        public readonly int FirstEntry = firstEntry;
        /// <summary>Index (into <c>_entryPositions</c>) of the last leaf entry under this subtree.</summary>
        public readonly int LastEntry = lastEntry;
    }
}
