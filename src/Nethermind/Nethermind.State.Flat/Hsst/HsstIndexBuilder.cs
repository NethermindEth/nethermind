// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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
    private readonly bool _isInline;
    private readonly ReadOnlySpan<byte> _inlineValueBuffer;
    private readonly ReadOnlySpan<int> _inlineValueLengths;

    public HsstIndexBuilder(ref TWriter writer, ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> entries, ReadOnlySpan<byte> separatorBuffer)
    {
        _writer = ref writer;
        _entries = entries;
        _separatorBuffer = separatorBuffer;
        _isInline = false;
        _inlineValueBuffer = default;
        _inlineValueLengths = default;
    }

    public HsstIndexBuilder(ref TWriter writer, ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> entries, ReadOnlySpan<byte> separatorBuffer,
        ReadOnlySpan<byte> inlineValueBuffer, ReadOnlySpan<int> inlineValueLengths)
    {
        _writer = ref writer;
        _entries = entries;
        _separatorBuffer = separatorBuffer;
        _isInline = true;
        _inlineValueBuffer = inlineValueBuffer;
        _inlineValueLengths = inlineValueLengths;
    }

    /// <summary>
    /// Build B-tree index via writer.
    /// The absolute data region start offset (= 1 + dataLen) is needed to compute child offsets.
    /// </summary>
    public void Build(int absoluteIndexStart, int maxLeafEntries = Hsst.MaxLeafEntries)
    {
        int startWritten = _writer.Written;

        if (_entries.Length == 0)
        {
            // Empty index: write a single empty leaf node
            WriteLeafIndexNode(ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry>.Empty, 0, 0);
            return;
        }

        // Build leaf nodes
        int maxNodes = (_entries.Length + maxLeafEntries - 1) / maxLeafEntries;
        Span<NodeInfo> currentLevel = stackalloc NodeInfo[maxNodes];
        Span<NodeInfo> nextLevel = stackalloc NodeInfo[maxNodes];
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
            int childOffset = (absoluteIndexStart + relativeStart + nodeLen) - 1;

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
                int childCount = Math.Min(maxLeafEntries, currentLevelCount - childIdx);
                ReadOnlySpan<NodeInfo> children = currentLevel.Slice(childIdx, childCount);

                int nodeStart = _writer.Written;
                int relativeStart = nodeStart - startWritten;
                WriteInternalIndexNode(children, _separatorBuffer);
                int nodeLen = _writer.Written - nodeStart;

                NodeInfo first = children[0];
                NodeInfo last = children[childCount - 1];

                int childOffset = (absoluteIndexStart + relativeStart + nodeLen) - 1;

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

    private void WriteLeafIndexNode(
        ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> entries,
        int absoluteNodeStart,
        int globalStartIndex)
    {
        if (_isInline)
        {
            WriteLeafIndexNodeInline(entries, globalStartIndex);
            return;
        }

        // Compute BaseOffset from values
        int baseOffset = 0;
        if (entries.Length > 1)
        {
            int minVal = entries[0].MetadataStart;
            int maxVal = minVal;
            for (int i = 1; i < entries.Length; i++)
            {
                if (entries[i].MetadataStart < minVal) minVal = entries[i].MetadataStart;
                if (entries[i].MetadataStart > maxVal) maxVal = entries[i].MetadataStart;
            }
            if (minVal > 0 && minVal < maxVal)
                baseOffset = minVal;
        }

        // Auto-select KeyType: all same non-zero length -> Uniform, else Variable
        // When max separator length <= 3, prefer UniformWithLen over Variable since
        // Variable has at least 3 bytes overhead per entry (2-byte offset + LEB128 length).
        int keyType = 0;
        int keySlotSize = 0;
        if (entries.Length > 0)
        {
            bool allSameLen = true;
            int firstLen = entries[0].SepLen;
            int maxLen = firstLen;
            for (int i = 1; i < entries.Length; i++)
            {
                if (entries[i].SepLen != firstLen) allSameLen = false;
                if (entries[i].SepLen > maxLen) maxLen = entries[i].SepLen;
            }
            if (allSameLen && firstLen > 0)
            {
                keyType = 1; // Uniform
                keySlotSize = firstLen;
            }
            else if (maxLen <= 3)
            {
                keyType = 2; // UniformWithLen
                keySlotSize = maxLen + 1;
            }
        }

        // Key buffer: 2 bytes (u16 length) + key bytes per entry
        int keyBufSize = 0;
        for (int i = 0; i < entries.Length; i++)
            keyBufSize += 2 + entries[i].SepLen;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        // Write node via BSearchIndexWriter
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = false,
            KeyType = keyType,
            BaseOffset = baseOffset,
            KeySlotSize = keySlotSize
        }, keyBuf);

        Span<byte> valueBuf = stackalloc byte[4];
        for (int i = 0; i < entries.Length; i++)
        {
            ReadOnlySpan<byte> key = _separatorBuffer.Slice(entries[i].SepOffset, entries[i].SepLen);
            BinaryPrimitives.WriteInt32LittleEndian(valueBuf, entries[i].MetadataStart - baseOffset);
            indexWriter.AddKey(key, valueBuf);
        }
        indexWriter.FinalizeNode();
    }

    private void WriteLeafIndexNodeInline(
        ReadOnlySpan<HsstBuilder<TWriter>.HsstEntry> entries,
        int globalStartIndex)
    {
        if (entries.Length == 0)
        {
            // Write empty node
            scoped BSearchIndexWriter<TWriter> emptyWriter = new(ref _writer, new BSearchIndexMetadata
            {
                IsIntermediate = false,
            }, Span<byte>.Empty);
            emptyWriter.FinalizeNode();
            return;
        }

        // Auto-select ValueType from value sizes
        int firstValLen = _inlineValueLengths[globalStartIndex];
        bool allSameValLen = true;
        int maxValLen = firstValLen;
        for (int i = 1; i < entries.Length; i++)
        {
            int len = _inlineValueLengths[globalStartIndex + i];
            if (len != firstValLen) allSameValLen = false;
            if (len > maxValLen) maxValLen = len;
        }

        int valueType, valueSlotSize;
        if (allSameValLen)
        {
            valueType = 1; // Uniform
            valueSlotSize = firstValLen;
        }
        else if (maxValLen <= 3)
        {
            valueType = 2; // UniformWithLen
            valueSlotSize = maxValLen + 1;
        }
        else
        {
            valueType = 0; // Variable
            valueSlotSize = 0;
        }

        // Auto-select KeyType
        int keyType = 0;
        int keySlotSize = 0;
        bool allSameKeyLen = true;
        int firstKeyLen = entries[0].SepLen;
        int maxKeyLen = firstKeyLen;
        for (int i = 1; i < entries.Length; i++)
        {
            if (entries[i].SepLen != firstKeyLen) allSameKeyLen = false;
            if (entries[i].SepLen > maxKeyLen) maxKeyLen = entries[i].SepLen;
        }
        if (allSameKeyLen && firstKeyLen > 0)
        {
            keyType = 1; // Uniform
            keySlotSize = firstKeyLen;
        }
        else if (maxKeyLen <= 3)
        {
            keyType = 2; // UniformWithLen
            keySlotSize = maxKeyLen + 1;
        }

        // Compute buffer sizes
        int keyBufSize = 0;
        int valueBufSize = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            keyBufSize += 2 + entries[i].SepLen;
            valueBufSize += 2 + _inlineValueLengths[globalStartIndex + i];
        }

        Span<byte> keyBuf = stackalloc byte[keyBufSize];
        Span<byte> valueBuf = stackalloc byte[valueBufSize];

        // Write node via BSearchIndexWriter with value buffering
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = false,
            KeyType = keyType,
            KeySlotSize = keySlotSize,
            BaseOffset = 0,
            ValueType = valueType,
            ValueSlotSize = valueSlotSize,
        }, keyBuf, valueBuf);

        for (int i = 0; i < entries.Length; i++)
        {
            ReadOnlySpan<byte> key = _separatorBuffer.Slice(entries[i].SepOffset, entries[i].SepLen);
            int valueOffset = entries[i].MetadataStart;
            int valueLen = _inlineValueLengths[globalStartIndex + i];
            ReadOnlySpan<byte> value = _inlineValueBuffer.Slice(valueOffset, valueLen);
            indexWriter.AddKey(key, value);
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

        // Auto-select KeyType
        // When max separator length <= 3, prefer UniformWithLen over Variable since
        // Variable has at least 3 bytes overhead per entry (2-byte offset + LEB128 length).
        int keyType;
        int keySlotSize;
        int maxSepLen = 0;
        for (int i = 0; i < childCount; i++)
            if (sepLengths[i] > maxSepLen) maxSepLen = sepLengths[i];

        bool hasEmptyFirst = sepLengths[0] == 0;
        if (!hasEmptyFirst)
        {
            bool allSameLen = true;
            int firstLen = sepLengths[0];
            for (int i = 1; i < childCount; i++)
            {
                if (sepLengths[i] != firstLen) { allSameLen = false; break; }
            }
            if (allSameLen && firstLen > 0) { keyType = 1; keySlotSize = firstLen; }
            else if (maxSepLen <= 3) { keyType = 2; keySlotSize = maxSepLen + 1; }
            else { keyType = 0; keySlotSize = 0; }
        }
        else if (childCount > 1)
        {
            bool allSameLenExceptFirst = true;
            int secondLen = sepLengths[1];
            for (int i = 2; i < childCount; i++)
            {
                if (sepLengths[i] != secondLen) { allSameLenExceptFirst = false; break; }
            }
            if (allSameLenExceptFirst && secondLen > 0) { keyType = 2; keySlotSize = secondLen + 1; }
            else if (maxSepLen <= 3) { keyType = 2; keySlotSize = maxSepLen + 1; }
            else { keyType = 0; keySlotSize = 0; }
        }
        else { keyType = 0; keySlotSize = 0; }

        // Compute BaseOffset from child offsets
        int minVal = children[0].ChildOffset;
        int maxVal = minVal;
        for (int i = 1; i < childCount; i++)
        {
            if (children[i].ChildOffset < minVal) minVal = children[i].ChildOffset;
            if (children[i].ChildOffset > maxVal) maxVal = children[i].ChildOffset;
        }
        int baseOffset = (minVal > 0 && minVal < maxVal) ? minVal : 0;

        // Key buffer: 2 bytes (u16 length) + separator bytes per child
        int keyBufSize = 2 * childCount + tempOffset;
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        // Write node via BSearchIndexWriter
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            IsIntermediate = true,
            KeyType = keyType,
            BaseOffset = baseOffset,
            KeySlotSize = keySlotSize
        }, keyBuf);

        Span<byte> valueBuf = stackalloc byte[4];
        for (int i = 0; i < childCount; i++)
        {
            ReadOnlySpan<byte> key = tempSepBuffer.Slice(sepOffsets[i], sepLengths[i]);
            BinaryPrimitives.WriteInt32LittleEndian(valueBuf, children[i].ChildOffset - baseOffset);
            indexWriter.AddKey(key, valueBuf);
        }
        indexWriter.FinalizeNode();
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

    internal readonly struct NodeInfo(int childOffset, HsstBuilder<TWriter>.HsstEntry firstEntry, HsstBuilder<TWriter>.HsstEntry lastEntry)
    {
        /// <summary>Absolute last byte position of this node in _data (= absoluteIndexStart + position + size - 1).</summary>
        public readonly int ChildOffset = childOffset;
        public readonly HsstBuilder<TWriter>.HsstEntry FirstEntry = firstEntry;
        public readonly HsstBuilder<TWriter>.HsstEntry LastEntry = lastEntry;
    }
}
