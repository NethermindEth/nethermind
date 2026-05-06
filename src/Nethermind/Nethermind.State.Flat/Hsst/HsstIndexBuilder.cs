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
    public void Build(int absoluteIndexStart, int maxLeafEntries = HsstBTreeOptions.DefaultMaxLeafEntries, int maxIntermediateEntries = HsstBTreeOptions.DefaultMaxIntermediateEntries)
    {
        int startWritten = _writer.Written;

        if (_entries.Length == 0)
        {
            // Empty index: write a single empty leaf node
            WriteLeafIndexNode([], 0, 0);
            return;
        }

        // Build leaf nodes
        int maxNodes = (_entries.Length + maxLeafEntries - 1) / maxLeafEntries;
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
                int count = Math.Min(maxLeafEntries, _entries.Length - entryIdx);
                ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> leafEntries = _entries.Slice(entryIdx, count);

                int nodeStart = _writer.Written;
                int relativeStart = nodeStart - startWritten;
                WriteLeafIndexNode(leafEntries, absoluteIndexStart + relativeStart, entryIdx);
                int nodeLen = _writer.Written - nodeStart;

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
                    int childCount = Math.Min(maxIntermediateEntries, currentLevelCount - childIdx);
                    ReadOnlySpan<NodeInfo> children = currentLevel.Slice(childIdx, childCount);

                    int nodeStart = _writer.Written;
                    int relativeStart = nodeStart - startWritten;
                    WriteInternalIndexNode(children, _separatorBuffer);
                    int nodeLen = _writer.Written - nodeStart;

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

    private void WriteLeafIndexNode(
        ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> entries,
        int absoluteNodeStart,
        int globalStartIndex)
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
        BSearchIndexLayoutPlanner.Plan(_separatorBuffer, sepOffsets, sepLengths,
            out int prefixLen, out int keyType, out int keySlotSize);
        ReadOnlySpan<byte> commonPrefix = prefixLen > 0
            ? _separatorBuffer.Slice(entries[0].SepOffset, prefixLen)
            : default;

        // Key buffer: 2 bytes (u16 length) + post-strip suffix bytes per entry.
        int keyBufSize = 0;
        for (int i = 0; i < entries.Length; i++)
            keyBufSize += 2 + (entries[i].SepLen - prefixLen);
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
            ReadOnlySpan<byte> sep = _separatorBuffer.Slice(entries[i].SepOffset, entries[i].SepLen);
            WriteUInt64LE(valueBuf, entries[i].MetadataStart - baseOffset, valueSlotSize);
            indexWriter.AddKey(sep[prefixLen..], valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
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
