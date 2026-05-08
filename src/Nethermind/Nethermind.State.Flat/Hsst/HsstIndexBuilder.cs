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
    private readonly int _minSepLen;

    public HsstIndexBuilder(ref TWriter writer, TReader reader, ReadOnlySpan<long> entryPositions, int minSepLen)
    {
        _writer = ref writer;
        _reader = reader;
        _entryPositions = entryPositions;
        _minSepLen = minSepLen;
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
        int maxIntermediateBytes = HsstBTreeOptions.DefaultMaxIntermediateBytes)
    {
        long startWritten = _writer.Written;

        if (_entryPositions.Length == 0)
        {
            // Empty index: write a single empty leaf node.
            return WriteEmptyLeafIndexNode();
        }

        if (minLeafEntries > maxLeafEntries) minLeafEntries = maxLeafEntries;
        if (minLeafEntries < 1) minLeafEntries = 1;

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

        // lastNodeLen tracks the byte length of the most recently written node; the
        // returned value is the root node's size (the last node emitted).
        int lastNodeLen = 0;

        try
        {
            int currentLevelCount = 0;
            int entryIdx = 0;

            // Running global previous key — feeds the first separator of each leaf.
            // Empty until the first entry is processed.
            Span<byte> prevKey = stackalloc byte[MaxKeyLen];
            int prevKeyLen = 0;
            // Phase-1 output: the leaf's last entry's full key. Hoisted out of the
            // loop to avoid per-iteration stackalloc.
            Span<byte> leafLastKey = stackalloc byte[MaxKeyLen];

            while (entryIdx < _entryPositions.Length)
            {
                // Phase 1: pick leaf size + naturalMax. Writes the leaf's last entry's
                // full key (the global predecessor for the next leaf) into leafLastKey.
                LeafLayout layout = ChooseLeafLayout(
                    entryIdx, minLeafEntries, maxLeafEntries,
                    prevKey[..prevKeyLen],
                    leafLastKey, out int leafLastKeyLen);
                int count = layout.Count;

                // The leaf is the root iff it consumes every remaining entry on the
                // very first iteration — i.e. there is exactly one leaf in total.
                bool isRoot = entryIdx == 0 && count == _entryPositions.Length;

                // Phase 2: emit leaf node bytes.
                long nodeStart = _writer.Written;
                long relativeStart = nodeStart - startWritten;
                WriteLeafIndexNode(
                    entryIdx, count, layout.NaturalMax,
                    prevKey[..prevKeyLen],
                    leafSepScratchArr, valueScratchArr,
                    isRoot);
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
                        maxIntermediateEntries, maxIntermediateBytes);
                    ReadOnlySpan<NodeInfo> children = currentLevel.Slice(childIdx, childCount);

                    // This node will be the root iff it covers the entire current level
                    // in one go — i.e. the next level has only this single node.
                    bool isRoot = childIdx == 0 && childCount == currentLevelCount;

                    long nodeStart = _writer.Written;
                    long relativeStart = nodeStart - startWritten;
                    WriteInternalIndexNode(children, internalSepScratchArr, valueScratchArr, isRoot);
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
    /// Reads each entry's full key on demand through the data-section reader and
    /// recomputes its natural separator length against the immediately-preceding
    /// key (deterministic: same answer the writer would have eagerly produced).
    /// </summary>
    private LeafLayout ChooseLeafLayout(
        int entryIdx, int minLeafEntries, int maxLeafEntries,
        scoped ReadOnlySpan<byte> globalPrevKey,
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
        int firstSepLen = HsstSeparator.ComputeSeparatorLength(globalPrevKey, currKey[..currKeyLen], default, _minSepLen);
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
            int nextSepLen = HsstSeparator.ComputeSeparatorLength(currKey[..currKeyLen], nextKey[..nextKeyLen], default, _minSepLen);

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

            if (count >= minLeafEntries &&
                (newMaxSepLen > maxSepLen || newCommonLen < commonLen || newValueSlotSize > valueSlotSize))
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

    /// <summary>
    /// Compute the prefix length any descent reaching a subtree spanning leaf entries
    /// [<paramref name="firstLeafIdx"/>, <paramref name="lastLeafIdx"/>] is guaranteed to
    /// match against the queried key. The bounds are the parent's separators around this
    /// subtree, computed via <see cref="WriteSeparatorBetween"/> over the adjacent leaf
    /// entries; their LCP is the descent-guaranteed prefix because K ∈ [s_left, s_right)
    /// and any K in that range shares LCP(s_left, s_right) with all stored keys
    /// (LCP-in-range lemma). Subtrees on the leftmost or rightmost descendant chain have
    /// an open bound and return 0.
    /// </summary>
    private int ComputeParentGuaranteedPrefixLen(int firstLeafIdx, int lastLeafIdx)
    {
        if (firstLeafIdx == 0) return 0;
        if (lastLeafIdx >= _entryPositions.Length - 1) return 0;

        Span<byte> leftPrev = stackalloc byte[MaxKeyLen];
        Span<byte> leftCurr = stackalloc byte[MaxKeyLen];
        Span<byte> rightPrev = stackalloc byte[MaxKeyLen];
        Span<byte> rightCurr = stackalloc byte[MaxKeyLen];
        int leftPrevLen = ReadKey(firstLeafIdx - 1, leftPrev);
        int leftCurrLen = ReadKey(firstLeafIdx, leftCurr);
        int rightPrevLen = ReadKey(lastLeafIdx, rightPrev);
        int rightCurrLen = ReadKey(lastLeafIdx + 1, rightCurr);

        Span<byte> sLeftBuf = stackalloc byte[MaxKeyLen];
        Span<byte> sRightBuf = stackalloc byte[MaxKeyLen];
        int sLeftLen = WriteSeparatorBetween(sLeftBuf, leftPrev[..leftPrevLen], leftCurr[..leftCurrLen]);
        int sRightLen = WriteSeparatorBetween(sRightBuf, rightPrev[..rightPrevLen], rightCurr[..rightCurrLen]);
        return CommonPrefixLength(sLeftBuf[..sLeftLen], sRightBuf[..sRightLen]);
    }

    private void WriteLeafIndexNode(
        int globalStartIndex, int count, int naturalMax,
        scoped ReadOnlySpan<byte> globalPrevKey,
        scoped Span<byte> leafSepScratch,
        scoped Span<byte> valueScratch,
        bool isRoot)
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
            int sepLen = HsstSeparator.ComputeSeparatorLength(prevKey[..prevKeyLen], currKey[..currKeyLen], default, _minSepLen);

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
        int parentGuaranteed = isRoot
            ? 0
            : ComputeParentGuaranteedPrefixLen(globalStartIndex, globalStartIndex + count - 1);
        BSearchIndexLayoutPlanner.Plan(sepView, sepOffsets, sepLengths,
            out int prefixLen, out int keyType, out int keySlotSize,
            disablePrefix: isRoot, parentGuaranteedPrefixLen: parentGuaranteed);

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
        }, keyBuf, valueScratchSlice, prefixLen);

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
        int maxChildren, int byteThreshold)
    {
        int remaining = level.Length - childIdx;
        int hardMax = Math.Min(maxChildren, remaining);
        if (hardMax <= 1) return hardMax;

        int childCount = 1;
        int sumSepBytes = 0;
        long minOff = level[childIdx].ChildOffset;
        long maxOff = minOff;

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
            long newMinOff = curr.ChildOffset < minOff ? curr.ChildOffset : minOff;
            int valueSlotSize = MinBytesFor(newMaxOff - newMinOff);

            int newCount = childCount + 1;
            int newSumSep = sumSepBytes + sepLen;
            int estimated = newCount * valueSlotSize + newSumSep;
            if (estimated > byteThreshold) break;

            childCount = newCount;
            sumSepBytes = newSumSep;
            maxOff = newMaxOff;
            minOff = newMinOff;
        }
        return childCount;
    }

    private void WriteInternalIndexNode(
        scoped ReadOnlySpan<NodeInfo> children,
        scoped Span<byte> sepScratch,
        scoped Span<byte> valueScratch,
        bool isRoot)
    {
        int childCount = children.Length;

        Span<int> sepOffsets = stackalloc int[childCount];
        Span<int> sepLengths = stackalloc int[childCount];
        int tempOffset = 0;

        Span<byte> leftKey = stackalloc byte[MaxKeyLen];
        Span<byte> rightKey = stackalloc byte[MaxKeyLen];

        sepOffsets[0] = 0;
        sepLengths[0] = 0;
        for (int i = 1; i < childCount; i++)
        {
            int leftLen = ReadKey(children[i - 1].LastEntry, leftKey);
            int rightLen = ReadKey(children[i].FirstEntry, rightKey);
            sepOffsets[i] = tempOffset;
            sepLengths[i] = WriteSeparatorBetween(sepScratch[tempOffset..], leftKey[..leftLen], rightKey[..rightLen]);
            tempOffset += sepLengths[i];
        }

        ReadOnlySpan<byte> sepView = sepScratch[..tempOffset];
        int parentGuaranteed = isRoot
            ? 0
            : ComputeParentGuaranteedPrefixLen(children[0].FirstEntry, children[childCount - 1].LastEntry);
        BSearchIndexLayoutPlanner.Plan(sepView, sepOffsets, sepLengths,
            out int prefixLen, out int keyType, out int keySlotSize,
            disablePrefix: isRoot, parentGuaranteedPrefixLen: parentGuaranteed);

        // Compute BaseOffset from child offsets, then choose the minimum byte width
        // that fits the in-node delta range.
        long minVal = children[0].ChildOffset;
        long maxVal = minVal;
        for (int i = 1; i < childCount; i++)
        {
            if (children[i].ChildOffset < minVal) minVal = children[i].ChildOffset;
            if (children[i].ChildOffset > maxVal) maxVal = children[i].ChildOffset;
        }
        long baseOffset = (minVal > 0 && minVal < maxVal) ? minVal : 0;
        int valueSlotSize = MinBytesFor(maxVal - baseOffset);

        int keyBufSize = 2 * childCount + tempOffset - prefixLen * childCount;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        Span<byte> valueScratchSlice = valueScratch[..(childCount * (2 + valueSlotSize))];
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = true,
            KeyType = keyType,
            BaseOffset = (ulong)baseOffset,
            KeySlotSize = keySlotSize,
            ValueType = 1,
            ValueSlotSize = valueSlotSize,
        }, keyBuf, valueScratchSlice, prefixLen);

        Span<byte> valueBuf = stackalloc byte[8];
        for (int i = 0; i < childCount; i++)
        {
            ReadOnlySpan<byte> sep = sepView.Slice(sepOffsets[i], sepLengths[i]);
            WriteUInt64LE(valueBuf, children[i].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(sep[prefixLen..], valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
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
    internal static int WriteSeparatorBetween(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
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
