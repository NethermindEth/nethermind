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
/// supplied reader. Separators (leaf-level disambiguators against the immediately
/// preceding entry) are recomputed on demand using
/// <see cref="HsstSeparator.ComputeSeparatorLength"/>; internal-node separators are
/// produced via <see cref="WriteSeparatorBetween"/> over the two boundary keys.
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
    // One byte per entry: natural separator length against the prior entry's key. Filled
    // once by PrecomputeSeparatorLengths at Build() entry and read by ChooseLeafLayout /
    // WriteLeafIndexNode instead of recomputing ComputeSeparatorLength twice per entry.
    // Rented from ArrayPool; returned in Build's finally.
    private byte[]? _sepLengthsArr;

    public HsstIndexBuilder(ref TWriter writer, TReader reader, ReadOnlySpan<long> entryPositions)
    {
        _writer = ref writer;
        _reader = reader;
        _entryPositions = entryPositions;
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

        // Build leaf nodes. minLeafEntries=maxLeafEntries reduces ChooseLeafCount to a fixed cap.
        // maxNodes is sized for the worst case: every leaf at minimum size.
        int maxNodes = (_entryPositions.Length + minLeafEntries - 1) / minLeafEntries;
        const int StackThreshold = 1024;
        NativeMemoryListRef<NodeInfo> currentNative = default;
        NativeMemoryListRef<NodeInfo> nextNative = default;
        scoped Span<NodeInfo> currentLevel;
        scoped Span<NodeInfo> nextLevel;
        if (maxNodes <= StackThreshold)
        {
            currentLevel = stackalloc NodeInfo[maxNodes];
            nextLevel = stackalloc NodeInfo[maxNodes];
        }
        else
        {
            currentNative = new NativeMemoryListRef<NodeInfo>(maxNodes, maxNodes);
            nextNative = new NativeMemoryListRef<NodeInfo>(maxNodes, maxNodes);
            currentLevel = currentNative.AsSpan();
            nextLevel = nextNative.AsSpan();
        }

        // Reusable per-leaf separator scratch. Holds concatenated separator bytes for
        // the leaf currently being written. Sized once to the worst-case leaf
        // (maxLeafEntries * MaxKeyLen) and reused across leaves; the in-use prefix
        // is the [..totalSepBytes] slice the caller computes per leaf.
        byte[] leafSepScratchArr = ArrayPool<byte>.Shared.Rent(Math.Max(64, maxLeafEntries * MaxKeyLen));

        // Reusable internal-node separator scratch. Internal separators are derived
        // via WriteSeparatorBetween (≤ MaxKeyLen each, ≤ maxIntermediateEntries entries).
        byte[] internalSepScratchArr = ArrayPool<byte>.Shared.Rent(Math.Max(64, maxIntermediateEntries * MaxKeyLen));

        // Reusable per-node value scratch. Each entry's value slot is at most 8 bytes
        // (Uniform offset width) plus a 2-byte u16 length prefix in the writer's buffer.
        // Sized for the larger of leaf/intermediate fan-out.
        int valueScratchEntries = Math.Max(maxLeafEntries, maxIntermediateEntries);
        byte[] valueScratchArr = ArrayPool<byte>.Shared.Rent(Math.Max(64, valueScratchEntries * (2 + 8)));

        _sepLengthsArr = ArrayPool<byte>.Shared.Rent(_entryPositions.Length);

        // lastNodeLen tracks the byte length of the most recently written node; the
        // returned value is the root node's size (the last node emitted).
        int lastNodeLen = 0;

        try
        {
            PrecomputeSeparatorLengths();

            int currentLevelCount = 0;
            int entryIdx = 0;

            // Running global previous key — feeds the first separator of each leaf.
            // Empty until the first entry is processed.
            Span<byte> prevKey = stackalloc byte[MaxKeyLen];
            int prevKeyLen = 0;
            // Phase-1 output: the leaf's last entry's full key. Hoisted out of the
            // loop to avoid per-iteration stackalloc.
            Span<byte> leafLastKey = stackalloc byte[MaxKeyLen];

            // True until the first node of the index region has been written.
            // Used to gate MaybePadToNextPage so we never pad after the root —
            // the trailer formula assumes [...root...][trailer] with no gap.
            bool firstNode = true;

            while (entryIdx < _entryPositions.Length)
            {
                // Phase 1: pick leaf size + naturalMax. Writes the leaf's last entry's
                // full key (the global predecessor for the next leaf) into leafLastKey.
                LeafLayout layout = ChooseLeafLayout(
                    entryIdx, minLeafEntries, maxLeafEntries,
                    _writer.Written, firstOffset,
                    leafLastKey, out int leafLastKeyLen);
                int count = layout.Count;

                // Pad to a fresh page if we're within PageAlignPadThreshold of
                // the boundary. Skipped on the first node — there's nothing to
                // pad away from yet.
                if (!firstNode) MaybePadToNextPage();
                firstNode = false;

                // Phase 2: emit leaf node bytes.
                long nodeStart = _writer.Written;
                long relativeStart = nodeStart - startWritten;
                WriteLeafIndexNode(
                    entryIdx, count, layout.NaturalMax,
                    prevKey[..prevKeyLen],
                    leafSepScratchArr, valueScratchArr);
                int nodeLen = checked((int)(_writer.Written - nodeStart));
                lastNodeLen = nodeLen;

                // childOffset = absolute first byte position of this node.
                long childOffset = absoluteIndexStart + relativeStart;

                currentLevel[currentLevelCount++] = new NodeInfo(
                    childOffset,
                    entryIdx,
                    entryIdx + count - 1);

                // Slide: prevKey ← leaf's last entry's full key (already in leafLastKey).
                leafLastKey[..leafLastKeyLen].CopyTo(prevKey);
                prevKeyLen = leafLastKeyLen;

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
                        _writer.Written, firstOffset);
                    ReadOnlySpan<NodeInfo> children = currentLevel.Slice(childIdx, childCount);

                    // Always non-first here (at least one leaf already written).
                    MaybePadToNextPage();

                    long nodeStart = _writer.Written;
                    long relativeStart = nodeStart - startWritten;
                    WriteInternalIndexNode(children, internalSepScratchArr, valueScratchArr);
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
            ArrayPool<byte>.Shared.Return(leafSepScratchArr);
            ArrayPool<byte>.Shared.Return(internalSepScratchArr);
            ArrayPool<byte>.Shared.Return(valueScratchArr);
            ArrayPool<byte>.Shared.Return(_sepLengthsArr);
            _sepLengthsArr = null;
        }

        return lastNodeLen;
    }

    /// <summary>
    /// Per-leaf layout decided by <see cref="ChooseLeafLayout"/>: how many entries
    /// to include and the natural max separator length used by the retry-truncate
    /// step inside <see cref="WriteLeafIndexNode"/>.
    /// </summary>
    private readonly struct LeafLayout(int count, int naturalMax)
    {
        public readonly int Count = count;
        public readonly int NaturalMax = naturalMax;
    }

    /// <summary>
    /// Pick the number of entries to pack into the next leaf and, in the same
    /// pass, compute the leaf's natural-disambiguation budget (max over consecutive
    /// pairs of <c>commonPrefix(sep[i-1], sep[i]) + 1</c>) used to retry-truncate
    /// stored separators.
    ///
    /// Reads each entry's full key on demand through the data-section reader; pulls
    /// the per-entry separator length from <see cref="_sepLengthsArr"/> (filled once
    /// by <see cref="PrecomputeSeparatorLengths"/>).
    /// </summary>
    private LeafLayout ChooseLeafLayout(
        int entryIdx, int minLeafEntries, int maxLeafEntries,
        long nodeStart, long firstOffset,
        scoped Span<byte> leafLastKeyOut, out int leafLastKeyLen)
    {
        int remaining = _entryPositions.Length - entryIdx;
        int hardMax = Math.Min(maxLeafEntries, remaining);
        if (hardMax <= 0)
        {
            leafLastKeyLen = 0;
            return new LeafLayout(0, 1);
        }

        // Bytes of the first separator. The leaf-wide common prefix is always a
        // prefix of these bytes, so we only need to track its length (commonLen).
        Span<byte> firstSep = stackalloc byte[MaxKeyLen];
        // Sliding window keys.
        Span<byte> currKey = stackalloc byte[MaxKeyLen];
        Span<byte> nextKey = stackalloc byte[MaxKeyLen];
        // Sep bytes of the entry at (entryIdx + count - 1) — needed for pair-level
        // disambiguation when its sep length equals the next entry's sep length.
        Span<byte> prevSep = stackalloc byte[MaxKeyLen];

        // Seed running state from the first entry alone.
        int currKeyLen = ReadKey(entryIdx, currKey);
        int firstSepLen = _sepLengthsArr![entryIdx];
        currKey[..firstSepLen].CopyTo(firstSep);
        currKey[..firstSepLen].CopyTo(prevSep);
        int prevSepLen = firstSepLen;

        int maxSepLen = firstSepLen;
        int naturalMax = 1;
        int commonLen = firstSepLen;

        // Mirror WriteLeafIndexNode's per-leaf metadata-offset width selection so we
        // stop before the next entry pushes every value slot up to a wider encoding.
        long minVal = _entryPositions[entryIdx];
        long maxVal = minVal;
        int valueSlotSize = MinBytesFor(0);

        int count = 1;
        while (count < hardMax)
        {
            int nextKeyLen = ReadKey(entryIdx + count, nextKey);
            int nextSepLen = _sepLengthsArr![entryIdx + count];

            int la = prevSepLen;
            int lb = nextSepLen;
            int pairNeeded;
            if (la == lb)
            {
                int common = CommonPrefixLength(prevSep[..la], nextKey[..lb]);
                pairNeeded = common + 1;
                if (pairNeeded > la) pairNeeded = la;
            }
            else
            {
                pairNeeded = Math.Max(la, lb);
            }
            int newNaturalMax = Math.Max(naturalMax, pairNeeded);

            int newMaxSepLen = Math.Max(maxSepLen, lb);
            int boundary = Math.Min(commonLen, lb);
            int newCommonLen = commonLen == 0
                ? 0
                : CommonPrefixLength(firstSep[..boundary], nextKey[..boundary]);

            long nextMd = _entryPositions[entryIdx + count];
            long newMinVal = Math.Min(minVal, nextMd);
            long newMaxVal = Math.Max(maxVal, nextMd);
            long newBase = (newMinVal > 0 && newMinVal < newMaxVal) ? newMinVal : 0;
            int newValueSlotSize = MinBytesFor(newMaxVal - newBase);

            // Conservative upper-bound size estimate for the candidate node (count+1
            // entries). Treats per-entry common-prefix strip as 0 (unknown until plan
            // time) and uses newMaxSepLen for every key — overestimates slightly,
            // but guarantees we never plan a node that crosses a 4 KiB page.
            int candidateCount = count + 1;
            int candidateSize = NodeSizeUpperBound(candidateCount, newMaxSepLen, newValueSlotSize);
            int committedSize = NodeSizeUpperBound(count, maxSepLen, valueSlotSize);

            // Encoding degrades only when the post-strip slot width grows past 4 — within
            // ≤ 4 B the planner stays on the SIMD-friendly Uniform ≤ 4 / UniformWithLen ≤ 4
            // paths, so any combination of (maxSepLen growth, commonLen shrink) that keeps
            // effMax = maxSepLen − commonLen ≤ 4 is safe. Only force-split on sep/prefix
            // signals when they push the effective slot above 4.
            int effMax = newMaxSepLen - newCommonLen;
            bool encodingForcesSplit = effMax > 4;
            if (count >= minLeafEntries &&
                (encodingForcesSplit || newValueSlotSize > valueSlotSize ||
                 WouldCrossNewPage(nodeStart, firstOffset, committedSize, candidateSize)))
                break;

            maxSepLen = newMaxSepLen;
            commonLen = newCommonLen;
            naturalMax = newNaturalMax;
            minVal = newMinVal;
            maxVal = newMaxVal;
            valueSlotSize = newValueSlotSize;

            // Slide window: curr ← next; prevSep ← next's sep bytes.
            nextKey[..nextKeyLen].CopyTo(currKey);
            currKeyLen = nextKeyLen;
            nextKey[..lb].CopyTo(prevSep);
            prevSepLen = lb;
            count++;
        }

        currKey[..currKeyLen].CopyTo(leafLastKeyOut);
        leafLastKeyLen = currKeyLen;
        return new LeafLayout(count, naturalMax);
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
        int globalStartIndex, int count, int naturalMax,
        scoped ReadOnlySpan<byte> globalPrevKey,
        scoped Span<byte> leafSepScratch,
        scoped Span<byte> valueScratch)
    {
        // Materialise separators for this leaf into the scratch buffer.
        // Each entry's separator is a prefix of its full key; computed against the
        // immediately preceding key (across leaf boundaries when i == 0).
        Span<int> sepOffsets = stackalloc int[count];
        Span<int> sepLengths = stackalloc int[count];

        Span<byte> prevKey = stackalloc byte[MaxKeyLen];
        int prevKeyLen = globalPrevKey.Length;
        globalPrevKey.CopyTo(prevKey);

        Span<byte> currKey = stackalloc byte[MaxKeyLen];

        // Simultaneously gather metadataStart values for value-slot sizing.
        Span<long> metadataStarts = stackalloc long[count];
        long minVal = long.MaxValue;
        long maxVal = 0;

        int totalSepBytes = 0;
        for (int i = 0; i < count; i++)
        {
            int globalIdx = globalStartIndex + i;
            int currKeyLen = ReadKey(globalIdx, currKey);
            int sepLen = _sepLengthsArr![globalIdx];

            sepOffsets[i] = totalSepBytes;
            sepLengths[i] = sepLen;
            currKey[..sepLen].CopyTo(leafSepScratch[totalSepBytes..]);
            totalSepBytes += sepLen;

            long mdStart = _entryPositions[globalIdx];
            metadataStarts[i] = mdStart;
            if (mdStart < minVal) minVal = mdStart;
            if (mdStart > maxVal) maxVal = mdStart;

            currKey[..currKeyLen].CopyTo(prevKey);
            prevKeyLen = currKeyLen;
        }

        long baseOffset = 0;
        if (count > 1 && minVal > 0 && minVal < maxVal) baseOffset = minVal;
        int valueSlotSize = MinBytesFor(maxVal - baseOffset);

        // Retry-truncate to naturalMax: lets the planner pick a tighter Uniform slot.
        for (int i = 0; i < count; i++)
        {
            if (sepLengths[i] > naturalMax) sepLengths[i] = naturalMax;
        }

        ReadOnlySpan<byte> sepView = leafSepScratch[..totalSepBytes];
        BSearchIndexLayoutPlanner.Plan(sepView, sepOffsets, sepLengths,
            out int prefixLen, out int keyType, out int keySlotSize, out bool keyLittleEndian);
        ReadOnlySpan<byte> commonPrefix = prefixLen > 0
            ? sepView.Slice(sepOffsets[0], prefixLen)
            : default;

        // Key buffer: 2 bytes (u16 length) + post-strip suffix bytes per entry.
        int keyBufSize = 0;
        for (int i = 0; i < count; i++)
            keyBufSize += 2 + (sepLengths[i] - prefixLen);
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        Span<byte> valueScratchSlice = valueScratch[..(count * (2 + valueSlotSize))];
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = false,
            KeyType = keyType,
            BaseOffset = (ulong)baseOffset,
            KeySlotSize = keySlotSize,
            ValueType = 1,
            ValueSlotSize = valueSlotSize,
            IsKeyLittleEndian = keyLittleEndian,
        }, keyBuf, valueScratchSlice, commonPrefix);

        Span<byte> valueBuf = stackalloc byte[8];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> sep = sepView.Slice(sepOffsets[i], sepLengths[i]);
            WriteUInt64LE(valueBuf, metadataStarts[i] - baseOffset, valueSlotSize);
            indexWriter.AddKey(sep[prefixLen..], valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
    }

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
        long nodeStart, long firstOffset)
    {
        int remaining = level.Length - childIdx;
        int hardMax = Math.Min(maxChildren, remaining);
        if (hardMax <= 1) return hardMax;

        int childCount = 1;
        int sumSepBytes = 0;
        // BaseOffset is fixed at the leftmost child's absolute offset; remaining
        // children encode as deltas. valueSlotSize tracks the min byte width for
        // the current max delta over children[1..].
        long baseChildOffset = level[childIdx].ChildOffset;
        long maxOff = baseChildOffset;
        int committedValueSlot = MinBytesFor(0);
        // Max separator length seen so far. Growth forces the planner to widen
        // its Uniform key slot or fall back to Variable layout, hurting binary
        // search density.
        int maxSepLen = 0;
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
        scoped Span<byte> sepScratch,
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

        Span<int> sepOffsets = stackalloc int[entryCount];
        Span<int> sepLengths = stackalloc int[entryCount];
        int tempOffset = 0;

        Span<byte> leftKey = stackalloc byte[MaxKeyLen];
        Span<byte> rightKey = stackalloc byte[MaxKeyLen];

        for (int i = 0; i < entryCount; i++)
        {
            int leftLen = ReadKey(children[i].LastEntry, leftKey);
            int rightLen = ReadKey(children[i + 1].FirstEntry, rightKey);
            sepOffsets[i] = tempOffset;
            sepLengths[i] = WriteSeparatorBetween(sepScratch[tempOffset..], leftKey[..leftLen], rightKey[..rightLen]);
            tempOffset += sepLengths[i];
        }

        ReadOnlySpan<byte> sepView = sepScratch[..tempOffset];
        BSearchIndexLayoutPlanner.Plan(sepView, sepOffsets, sepLengths,
            out int prefixLen, out int keyType, out int keySlotSize, out bool keyLittleEndian);
        ReadOnlySpan<byte> commonPrefix = prefixLen > 0
            ? sepView.Slice(sepOffsets[0], prefixLen)
            : default;

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

        int keyBufSize = 2 * entryCount + tempOffset - prefixLen * entryCount;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        Span<byte> valueScratchSlice = valueScratch[..(entryCount * (2 + valueSlotSize))];
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = true,
            KeyType = keyType,
            BaseOffset = (ulong)baseOffset,
            KeySlotSize = keySlotSize,
            ValueType = 1,
            ValueSlotSize = valueSlotSize,
            IsKeyLittleEndian = keyLittleEndian,
        }, keyBuf, valueScratchSlice, commonPrefix);

        Span<byte> valueBuf = stackalloc byte[8];
        for (int i = 0; i < entryCount; i++)
        {
            ReadOnlySpan<byte> sep = sepView.Slice(sepOffsets[i], sepLengths[i]);
            WriteUInt64LE(valueBuf, children[i + 1].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(sep[prefixLen..], valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
    }

    /// <summary>
    /// One-pass pre-computation of per-entry natural separator length against the prior
    /// entry's key. Writes into
    /// <see cref="_sepLengthsArr"/> (one byte per entry — fits because
    /// <see cref="HsstSeparator.ComputeSeparatorLength"/> caps at currKey.Length ≤
    /// <see cref="MaxKeyLen"/> = 255). Both <see cref="ChooseLeafLayout"/> and
    /// <see cref="WriteLeafIndexNode"/> read from this table instead of recomputing the
    /// same value twice per entry.
    /// </summary>
    private void PrecomputeSeparatorLengths()
    {
        int n = _entryPositions.Length;
        Span<byte> prevKey = stackalloc byte[MaxKeyLen];
        Span<byte> currKey = stackalloc byte[MaxKeyLen];
        int prevKeyLen = 0;
        for (int i = 0; i < n; i++)
        {
            int currKeyLen = ReadKey(i, currKey);
            int sepLen = HsstSeparator.ComputeSeparatorLength(
                prevKey[..prevKeyLen], currKey[..currKeyLen], default);
            _sepLengthsArr![i] = (byte)sepLen;
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

    private static void ThrowReadFailed()
        => throw new IOException("HSST data-section read out of range during index build.");

    // Conservative upper bound on BSearchIndexWriter header bytes: 12 base
    // (Flags + KeyCount u16 + KeySize u16 + ValueSize u8 + BaseOffset 6) + 1
    // optional CommonPrefixLen byte + a small slack.
    private const int NodeHeaderUpperBound = 16;

    // Conservative upper bound on a leaf node's serialised size given a candidate
    // entry count, max separator length, and value slot size. Treats common prefix
    // as 0 (unknown until plan-time) and uses Uniform layouts (no offset table).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NodeSizeUpperBound(int count, int maxSepLen, int valueSlotSize)
        => NodeHeaderUpperBound + count * (maxSepLen + valueSlotSize);

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
        // separators stay uniform when the caller has signalled a fixed key width —
        // matching the leaf-side floor in HsstSeparator.ComputeSeparatorLength.
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
