// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.BSearchIndex;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds the B-tree index region for an HSST block.
/// Takes (separator, metadataStart) leaf entries and produces a complete index region
/// where the root index is the last block (readable from end via MetadataLength byte).
/// </summary>
public ref struct HsstIndexBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    private ref TWriter _writer;
    private readonly ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> _entries;
    private readonly ReadOnlySpan<byte> _separatorBuffer;

    public HsstIndexBuilder(ref TWriter writer, ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> entries, ReadOnlySpan<byte> separatorBuffer)
    {
        _writer = ref writer;
        _entries = entries;
        _separatorBuffer = separatorBuffer;
    }

    /// <summary>
    /// Build B-tree index via writer.
    /// The absolute data region start offset (= 1 + dataLen) is needed to compute child offsets.
    /// </summary>
    public void Build(int absoluteIndexStart, int maxLeafEntries = HsstBTreeOptions.DefaultMaxLeafEntries, int maxIntermediateEntries = HsstBTreeOptions.DefaultMaxIntermediateEntries, int minLeafEntries = HsstBTreeOptions.DefaultMinLeafEntries, int maxIntermediateBytes = HsstBTreeOptions.DefaultMaxIntermediateBytes)
    {
        long startWritten = _writer.Written;

        if (_entries.Length == 0)
        {
            // Empty index: write a single empty leaf node
            WriteLeafIndexNode([], 0, 0, naturalMax: 1);
            return;
        }

        if (minLeafEntries > maxLeafEntries) minLeafEntries = maxLeafEntries;
        if (minLeafEntries < 1) minLeafEntries = 1;

        // Build leaf nodes. minLeafEntries=maxLeafEntries reduces ChooseLeafCount to a fixed cap.
        // maxNodes is sized for the worst case: every leaf at minimum size.
        int maxNodes = (_entries.Length + minLeafEntries - 1) / minLeafEntries;
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

        try
        {
            int currentLevelCount = 0;

            int entryIdx = 0;

            while (entryIdx < _entries.Length)
            {
                LeafLayout layout = ChooseLeafLayout(entryIdx, minLeafEntries, maxLeafEntries);
                int count = layout.Count;
                ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> leafEntries = _entries.Slice(entryIdx, count);

                long nodeStart = _writer.Written;
                // Per-HSST cap is ≤2 GiB so the node-relative offsets fit in int.
                int relativeStart = (int)(nodeStart - startWritten);
                WriteLeafIndexNode(leafEntries, absoluteIndexStart + relativeStart, entryIdx, layout.NaturalMax);
                int nodeLen = (int)(_writer.Written - nodeStart);

                HsstBuilder<TWriter>.HsstEntry first = leafEntries[0];
                HsstBuilder<TWriter>.HsstEntry last = leafEntries[count - 1];

                // childOffset = absolute last byte position of this node
                ulong childOffset = (ulong)(absoluteIndexStart + relativeStart + nodeLen) - 1UL;

                currentLevel[currentLevelCount++] = new NodeInfo(
                    childOffset,
                    first,
                    last);

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

                    long nodeStart = _writer.Written;
                    // Per-HSST cap is ≤2 GiB so the node-relative offsets fit in int.
                    int relativeStart = (int)(nodeStart - startWritten);
                    WriteInternalIndexNode(children, _separatorBuffer);
                    int nodeLen = (int)(_writer.Written - nodeStart);

                    NodeInfo first = children[0];
                    NodeInfo last = children[childCount - 1];

                    ulong childOffset = (ulong)(absoluteIndexStart + relativeStart + nodeLen) - 1UL;

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
        }
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
    /// Inclusion rules:
    ///  - The first <paramref name="minLeafEntries"/> entries are unconditional
    ///    (or fewer if input is exhausted).
    ///  - Past that watermark, split early when:
    ///     - the next entry's separator length would push the running max
    ///       separator length up (a longer-than-current separator forces every
    ///       entry into a larger Uniform slot post-truncate), or
    ///     - the next entry's separator would shrink the running common-prefix
    ///       (the planner's prefix-strip would expose more bytes per entry).
    ///  - Capped at <paramref name="maxLeafEntries"/>.
    ///
    /// <c>NaturalMax</c> covers exactly the included pairs; it equals the
    /// per-leaf max disambiguation needed to keep in-leaf sort order intact when
    /// the planner picks a uniform slot.
    /// </summary>
    private LeafLayout ChooseLeafLayout(int entryIdx, int minLeafEntries, int maxLeafEntries)
    {
        int remaining = _entries.Length - entryIdx;
        int hardMax = Math.Min(maxLeafEntries, remaining);
        if (hardMax <= 0) return new LeafLayout(0, 1);

        // Seed running state from the first entry alone.
        HsstBuilder<TWriter>.HsstEntry firstEntry = _entries[entryIdx];
        int maxSepLen = firstEntry.SepLen;
        int naturalMax = 1;
        ReadOnlySpan<byte> commonPrefix = _separatorBuffer.Slice(firstEntry.SepOffset, firstEntry.SepLen);
        int commonLen = commonPrefix.Length;

        int count = 1;
        while (count < hardMax)
        {
            HsstBuilder<TWriter>.HsstEntry prev = _entries[entryIdx + count - 1];
            HsstBuilder<TWriter>.HsstEntry curr = _entries[entryIdx + count];
            int la = prev.SepLen;
            int lb = curr.SepLen;
            ReadOnlySpan<byte> currSep = _separatorBuffer.Slice(curr.SepOffset, lb);

            // Pair-level natural disambiguation. When stored lengths differ,
            // the shorter side may hide divergence past its end — fall back to
            // max(la, lb) to be safe (mirrors the retry-truncate logic).
            int pairNeeded;
            if (la == lb)
            {
                ReadOnlySpan<byte> prevSep = _separatorBuffer.Slice(prev.SepOffset, la);
                int common = prevSep.CommonPrefixLength(currSep);
                pairNeeded = common + 1;
                if (pairNeeded > la) pairNeeded = la;
            }
            else
            {
                pairNeeded = Math.Max(la, lb);
            }
            int newNaturalMax = Math.Max(naturalMax, pairNeeded);

            // Running max separator length and common-prefix length after
            // hypothetically including curr.
            int newMaxSepLen = Math.Max(maxSepLen, lb);
            int boundary = Math.Min(commonLen, lb);
            int newCommonLen = commonLen == 0
                ? 0
                : commonPrefix[..boundary].CommonPrefixLength(currSep[..boundary]);

            // Past min watermark, split if either metric would worsen.
            if (count >= minLeafEntries && (newMaxSepLen > maxSepLen || newCommonLen < commonLen))
                break;

            // Commit.
            maxSepLen = newMaxSepLen;
            commonLen = newCommonLen;
            commonPrefix = commonPrefix[..commonLen];
            naturalMax = newNaturalMax;
            count++;
        }

        return new LeafLayout(count, naturalMax);
    }

    private void WriteLeafIndexNode(
        ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> entries,
        int absoluteNodeStart,
        int globalStartIndex,
        int naturalMax)
    {
        // Compute BaseOffset from values, then pick the smallest 1..8 byte slot
        // width that can encode (max - baseOffset).
        ulong baseOffset = 0;
        ulong maxVal = 0;
        if (entries.Length > 0)
        {
            ulong minVal = entries[0].MetadataStart;
            maxVal = minVal;
            for (int i = 1; i < entries.Length; i++)
            {
                if (entries[i].MetadataStart < minVal) minVal = entries[i].MetadataStart;
                if (entries[i].MetadataStart > maxVal) maxVal = entries[i].MetadataStart;
            }
            if (entries.Length > 1 && minVal > 0 && minVal < maxVal)
                baseOffset = minVal;
        }
        int valueSlotSize = MinBytesFor(maxVal - baseOffset);

        // Decide CommonKeyPrefix and KeyType jointly against post-strip lengths.
        Span<int> sepOffsets = stackalloc int[entries.Length];
        Span<int> sepLengths = stackalloc int[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            sepOffsets[i] = entries[i].SepOffset;
            sepLengths[i] = entries[i].SepLen;
        }

        // Retry-truncate: <paramref name="naturalMax"/> was computed up-front by
        // ChooseLeafLayout (single pass over the same entries). Truncating each
        // stored separator down to it lets the planner pick a tighter Uniform
        // slot while keeping in-leaf sort order intact.
        for (int i = 0; i < entries.Length; i++)
        {
            if (sepLengths[i] > naturalMax) sepLengths[i] = naturalMax;
        }

        BSearchIndexLayoutPlanner.Plan(_separatorBuffer, sepOffsets, sepLengths,
            out int prefixLen, out int keyType, out int keySlotSize);
        ReadOnlySpan<byte> commonPrefix = prefixLen > 0
            ? _separatorBuffer.Slice(sepOffsets[0], prefixLen)
            : default;

        // Key buffer: 2 bytes (u16 length) + post-strip suffix bytes per entry.
        int keyBufSize = 0;
        for (int i = 0; i < entries.Length; i++)
            keyBufSize += 2 + (sepLengths[i] - prefixLen);
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = false,
            KeyType = keyType,
            BaseOffset = baseOffset,
            KeySlotSize = keySlotSize,
            ValueType = 1,
            ValueSlotSize = valueSlotSize,
        }, keyBuf, commonPrefix);

        Span<byte> valueBuf = stackalloc byte[8];
        for (int i = 0; i < entries.Length; i++)
        {
            ReadOnlySpan<byte> sep = _separatorBuffer.Slice(sepOffsets[i], sepLengths[i]);
            WriteUInt64LE(valueBuf, entries[i].MetadataStart - baseOffset, valueSlotSize);
            indexWriter.AddKey(sep[prefixLen..], valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
    }

    /// <summary>
    /// Pick the number of children to pack into the next intermediate node by
    /// summing values + keys section bytes until the next child would push the
    /// estimate over <paramref name="byteThreshold"/> (capped at
    /// <paramref name="maxChildren"/>; always includes at least one child).
    /// Footer/BaseOffset overhead is intentionally ignored — it's a fixed tax
    /// per node, doesn't affect packing decisions.
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
        ulong minOff = level[childIdx].ChildOffset;
        ulong maxOff = minOff;

        Span<byte> sepBuf = stackalloc byte[256];
        while (childCount < hardMax)
        {
            NodeInfo prev = level[childIdx + childCount - 1];
            NodeInfo curr = level[childIdx + childCount];
            ReadOnlySpan<byte> leftKey = _separatorBuffer.Slice(
                prev.LastEntry.SepOffset, prev.LastEntry.SepLen);
            ReadOnlySpan<byte> rightKey = _separatorBuffer.Slice(
                curr.FirstEntry.SepOffset, curr.FirstEntry.SepLen);
            int sepLen = WriteSeparatorBetween(sepBuf, leftKey, rightKey);

            ulong newMaxOff = curr.ChildOffset > maxOff ? curr.ChildOffset : maxOff;
            ulong newMinOff = curr.ChildOffset < minOff ? curr.ChildOffset : minOff;
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
        ReadOnlySpan<byte> separatorBuffer)
    {
        int childCount = children.Length;

        // Compute separators for each child
        int maxSepSize = 256;
        Span<byte> tempSepBuffer = stackalloc byte[maxSepSize * childCount];
        Span<int> sepOffsets = stackalloc int[childCount];
        Span<int> sepLengths = stackalloc int[childCount];
        int tempOffset = 0;

        sepOffsets[0] = 0;
        sepLengths[0] = 0;
        for (int i = 1; i < childCount; i++)
        {
            ReadOnlySpan<byte> leftKey = separatorBuffer.Slice(
                children[i - 1].LastEntry.SepOffset,
                children[i - 1].LastEntry.SepLen);
            ReadOnlySpan<byte> rightKey = separatorBuffer.Slice(
                children[i].FirstEntry.SepOffset,
                children[i].FirstEntry.SepLen);
            sepOffsets[i] = tempOffset;
            sepLengths[i] = WriteSeparatorBetween(tempSepBuffer[tempOffset..], leftKey, rightKey);
            tempOffset += sepLengths[i];
        }

        // Decide CommonKeyPrefix and KeyType jointly against post-strip lengths.
        BSearchIndexLayoutPlanner.Plan(tempSepBuffer, sepOffsets, sepLengths,
            out int prefixLen, out int keyType, out int keySlotSize);
        ReadOnlySpan<byte> commonPrefix = prefixLen > 0
            ? tempSepBuffer.Slice(sepOffsets[0], prefixLen)
            : default;

        // Compute BaseOffset from child offsets, then choose the minimum byte width
        // that fits the in-node delta range.
        ulong minVal = children[0].ChildOffset;
        ulong maxVal = minVal;
        for (int i = 1; i < childCount; i++)
        {
            if (children[i].ChildOffset < minVal) minVal = children[i].ChildOffset;
            if (children[i].ChildOffset > maxVal) maxVal = children[i].ChildOffset;
        }
        ulong baseOffset = (minVal > 0 && minVal < maxVal) ? minVal : 0;
        int valueSlotSize = MinBytesFor(maxVal - baseOffset);

        // Key buffer: 2 bytes (u16 length) + post-strip suffix bytes per child.
        int keyBufSize = 2 * childCount + tempOffset - prefixLen * childCount;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = true,
            KeyType = keyType,
            BaseOffset = baseOffset,
            KeySlotSize = keySlotSize,
            ValueType = 1,
            ValueSlotSize = valueSlotSize,
        }, keyBuf, commonPrefix);

        Span<byte> valueBuf = stackalloc byte[8];
        for (int i = 0; i < childCount; i++)
        {
            ReadOnlySpan<byte> sep = tempSepBuffer.Slice(sepOffsets[i], sepLengths[i]);
            WriteUInt64LE(valueBuf, children[i].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(sep[prefixLen..], valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
    }

    /// <summary>
    /// Smallest 1..8 byte width that can encode <paramref name="value"/>. Returns 1 for 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinBytesFor(ulong value)
    {
        if (value == 0) return 1;
        return ((BitOperations.Log2(value)) >> 3) + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64LE(Span<byte> dest, ulong value, int width)
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

    internal readonly struct NodeInfo(ulong childOffset, HsstBuilder<TWriter>.HsstEntry firstEntry, HsstBuilder<TWriter>.HsstEntry lastEntry)
    {
        /// <summary>Absolute last byte position of this node in _data (= absoluteIndexStart + position + size - 1).</summary>
        public readonly ulong ChildOffset = childOffset;
        public readonly HsstBuilder<TWriter>.HsstEntry FirstEntry = firstEntry;
        public readonly HsstBuilder<TWriter>.HsstEntry LastEntry = lastEntry;
    }
}
