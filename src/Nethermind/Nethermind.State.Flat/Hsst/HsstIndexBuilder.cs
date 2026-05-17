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
using Nethermind.State.Flat.Storage;

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
/// <c>min(commonPrefix + 1, currKeyLen)</c>. Internal-node separators are derived
/// the same way — adjacency of <c>NodeInfo</c> ranges means
/// <c>_commonPrefixArr[curr.FirstEntry]</c> already holds the LCP between the
/// left-subtree's last key and the right-subtree's first key; the separator bytes
/// are taken from the right-subtree's first key (cached in <c>_leafFirstKeys</c>).
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
    // Fixed key length for every entry (HsstBTreeBuilder enforces uniformity, and the
    // HSST trailer records the same value so readers don't need a per-entry length
    // byte). Used directly wherever we previously tracked minKeyLen — those collapse
    // to this single scalar.
    private readonly int _keyLength;
    // When true, entryPositions point to EntryStart (FullKey byte 0) and entry bytes
    // are [FullKey][LEB128 ValueLength][Value]. When false (default), entryPositions
    // point to MetadataStart (LEB128 byte) and bytes are [Value][LEB128][FullKey].
    private readonly bool _keyFirst;
    // Pointer to the caller-supplied buffers struct holding the work arrays/lists
    // (CommonPrefixArr, LeafFirstKeys, CurrentLevel, NextLevel, ValueScratch, SegTree,
    // DfsStack). Stored as void* because HsstBTreeBuilderBuffers is a ref struct and
    // therefore not eligible for ordinary T* / managed-pointer fields.
    private readonly unsafe void* _buffersPtr;

    public unsafe HsstIndexBuilder(ref TWriter writer, TReader reader, ReadOnlySpan<long> entryPositions, int keyLength, scoped ref HsstBTreeBuilderBuffers buffers, bool keyFirst = false)
    {
        _writer = ref writer;
        _reader = reader;
        _entryPositions = entryPositions;
        _keyLength = keyLength;
        _keyFirst = keyFirst;
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
    /// <c>[RootSize u16][KeyLength u8][IndexType u8]</c> trailer using that value so readers
    /// can locate the root from the HSST end.
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

        ref HsstBTreeBuilderBuffers bufs = ref Buffers;

        // Reusable per-node value scratch. Each entry's value slot is at most 8 bytes
        // (Uniform offset width) plus a 2-byte u16 length prefix in the writer's buffer.
        // Sized for the larger of leaf/intermediate fan-out.
        int valueScratchEntries = Math.Max(maxLeafEntries, maxIntermediateEntries);
        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.ValueScratch, Math.Max(64, valueScratchEntries * (2 + 8)));
        byte[] valueScratchArr = bufs.ValueScratch!;

        HsstBTreeBuilderBuffers.EnsureSize(ref bufs.CommonPrefixArr, n);
        byte[] commonPrefixArr = bufs.CommonPrefixArr!;

        // Leaf-level / intermediate-level node lists live on the buffers struct and are
        // cleared on each new builder construction by ResetForBuild; capacity persists
        // across builds. Swap roles via ref locals to avoid copying the structs.
        ref NativeMemoryListRef<HsstIndexNodeInfo> currentNative = ref bufs.CurrentLevel;
        ref NativeMemoryListRef<HsstIndexNodeInfo> nextNative = ref bufs.NextLevel;

        // lastNodeLen tracks the byte length of the most recently written node; the
        // returned value is the root node's size (the last node emitted).
        int lastNodeLen = 0;

        PrecomputeCommonPrefixLengths(commonPrefixArr);

        // The enumerator borrows the LCP segment tree and DFS stack from the buffers
        // struct (sized on demand in its constructor). Leaf sizes stream out via
        // MoveNext / Current, one at a time, directly into the emission loop.
        using LeafBoundaryEnumerator iter = new(
            commonPrefixArr, _entryPositions, n, minLeafEntries, maxLeafEntries, _keyLength, ref bufs);

        int entryIdx = 0;
        int leafIdx = 0;

        // True until the first node of the index region has been written.
        // Used to gate MaybePadToNextPage so we never pad after the root —
        // the trailer formula assumes [...root...][trailer] with no gap.
        bool firstNode = true;

        while (true)
        {
            // Bytes already written into the current 4 KiB page, fed into the
            // leaf splitter so it can force-split a leaf that would otherwise
            // straddle a page boundary (mirrors the intermediate-node path's
            // WouldCrossNewPage gate). Computed pre-pad — over-triggers in the
            // ≤ PageLayout.PadThreshold close-to-edge case, which is benign.
            long pageOff = (_writer.Written - firstOffset) & PageLayout.PageMask;
            if (!iter.MoveNext(pageOff)) break;
            int count = iter.Current;

            // Pad to a fresh page if we're within PageLayout.PadThreshold of
            // the boundary. Skipped on the first node — there's nothing to
            // pad away from yet.
            if (!firstNode) MaybePadToNextPage();
            firstNode = false;

            long nodeStart = _writer.Written;
            long relativeStart = nodeStart - startWritten;
            WriteLeafIndexNode(
                entryIdx, count,
                valueScratchArr, commonPrefixArr, ref bufs.LeafFirstKeys);
            int nodeLen = checked((int)(_writer.Written - nodeStart));
            lastNodeLen = nodeLen;

            // childOffset = absolute first byte position of this node.
            long childOffset = absoluteIndexStart + relativeStart;

            currentNative.Add(new HsstIndexNodeInfo(
                childOffset,
                entryIdx,
                entryIdx + count - 1,
                leafIdx));

            entryIdx += count;
            leafIdx++;
        }

        // Build internal levels until single root. Each iteration consumes
        // currentNative as a read-only span and accumulates the next level into
        // nextNative; swap the two ref locals at end of iteration.
        while (currentNative.Count > 1)
        {
            nextNative.Clear();
            ReadOnlySpan<HsstIndexNodeInfo> current = currentNative.AsSpan();
            int childIdx = 0;

            while (childIdx < current.Length)
            {
                int childCount = ChooseIntermediateChildCount(
                    current, childIdx,
                    maxIntermediateEntries, maxIntermediateBytes,
                    minIntermediateChildren, minIntermediateBytes,
                    _writer.Written, firstOffset,
                    commonPrefixArr, ref bufs.LeafFirstKeys,
                    out int crossEntryLcp);
                ReadOnlySpan<HsstIndexNodeInfo> children = current.Slice(childIdx, childCount);

                // Always non-first here (at least one leaf already written).
                MaybePadToNextPage();

                long nodeStart = _writer.Written;
                long relativeStart = nodeStart - startWritten;
                WriteInternalIndexNode(children, crossEntryLcp, valueScratchArr,
                    commonPrefixArr, ref bufs.LeafFirstKeys);
                int nodeLen = checked((int)(_writer.Written - nodeStart));
                lastNodeLen = nodeLen;

                HsstIndexNodeInfo first = children[0];
                HsstIndexNodeInfo last = children[childCount - 1];

                long childOffset = absoluteIndexStart + relativeStart;

                nextNative.Add(new HsstIndexNodeInfo(
                    childOffset,
                    first.FirstEntry,
                    last.LastEntry,
                    first.FirstLeafIdx));

                childIdx += childCount;
            }

            // Swap roles for the next level — ref reassignment, no struct copy.
            ref NativeMemoryListRef<HsstIndexNodeInfo> tmp = ref currentNative;
            currentNative = ref nextNative;
            nextNative = ref tmp;
        }

        return lastNodeLen;
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
        scoped Span<byte> valueScratch,
        byte[] commonPrefixArr,
        scoped ref NativeMemoryListRef<byte> leafFirstKeys)
    {
        // Per-entry natural separator length, capped at _keyLength: min(LCP(prev,curr)+1, key).
        // Widening to slot=4 (when applicable) is the planner's call now.
        Span<int> sepLengths = stackalloc int[count];
        for (int i = 0; i < count; i++)
            sepLengths[i] = Math.Min(commonPrefixArr[globalStartIndex + i] + 1, _keyLength);

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

        int crossEntryLcp = ComputeCrossEntryLcpLeaf(globalStartIndex, count, commonPrefixArr);
        BSearchIndexLayoutPlanner.Plan(sepLengths, crossEntryLcp, _keyLength,
            out int prefixLen, out int keyType, out int keySlotSize, out bool keyLittleEndian);

        // Pass 2: ReadKey + AddKey. Entry 0's ReadKey also feeds commonPrefix. The planner's
        // keySlotSize (post-widen, post-strip) drives slice width — may exceed sepLengths[i]
        // when the planner widened, in which case we read more bytes from the key.
        Span<byte> currKey = stackalloc byte[MaxKeyLen];
        Span<byte> commonPrefixBuf = stackalloc byte[prefixLen];

        // keyBuf must fit the widest per-entry payload across layouts: Uniform takes
        // keySlotSize bytes, Variable takes the per-entry natural sep length
        // (up to _keyLength - prefixLen). Use the max so all paths fit.
        int perEntryKeyBytes = Math.Max(keySlotSize, _keyLength - prefixLen);
        int keyBufSize = count * (2 + perEntryKeyBytes);
        Span<byte> keyBuf = stackalloc byte[keyBufSize];
        Span<byte> valueScratchSlice = valueScratch[..(count * (2 + valueSlotSize))];

        ReadKey(globalStartIndex, currKey);
        currKey[..prefixLen].CopyTo(commonPrefixBuf);
        // Persist this leaf's first key for intermediate-node construction. Keys are
        // uniform length, so the slot at leafIdx is leafFirstKeys[leafIdx*_keyLength..].
        // Appending in leaf-emission order keeps that invariant without an explicit index.
        leafFirstKeys.AddRange(currKey[.._keyLength]);

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
        scoped ReadOnlySpan<HsstIndexNodeInfo> level, int childIdx,
        int maxChildren, int byteThreshold,
        int minChildren, int minBytes,
        long nodeStart, long firstOffset,
        byte[] commonPrefixArr,
        scoped ref NativeMemoryListRef<byte> leafFirstKeys,
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

        Span<byte> sepBuf = stackalloc byte[MaxKeyLen];
        ReadOnlySpan<byte> leafKeys = leafFirstKeys.AsSpan();

        while (childCount < hardMax)
        {
            HsstIndexNodeInfo curr = level[childIdx + childCount];
            // Adjacency invariant: prev.LastEntry == curr.FirstEntry - 1, so
            // commonPrefixArr[curr.FirstEntry] is exactly LCP(leftKey, rightKey).
            // Separator length is min(LCP + 1, _keyLength); separator bytes are
            // rightKey[..sepLen] — leftKey is never observed downstream.
            ReadOnlySpan<byte> rightKey = leafKeys.Slice(curr.FirstLeafIdx * _keyLength, _keyLength);
            int sepLen = Math.Min(commonPrefixArr[curr.FirstEntry] + 1, _keyLength);
            rightKey[..sepLen].CopyTo(sepBuf);

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

            // Dynamic split heuristics. Once minChildren is reached, break only
            // when:
            //   - effective separator (post-LCP-strip) would exceed 4 bytes —
            //     mirrors the leaf splitter's `gap > 4` rule. Combines the old
            //     "max sep widened" and "LCP shrank" checks into a single
            //     post-strip-width budget; value-slot widening is allowed.
            //   - WouldCrossNewPage: candidate node would straddle a 4 KiB page
            //     boundary the committed node does not.
            int newEffSepLen = newMaxSepLen - newCommonLen;
            int candidateSize = IntermediateNodeSizeUpperBound(newCount, newSumSep, valueSlotSize);
            int committedSize = IntermediateNodeSizeUpperBound(childCount, sumSepBytes, committedValueSlot);
            if (childCount >= minChildren &&
                committedSize >= minBytes &&
                (newEffSepLen > 4 ||
                 WouldCrossNewPage(nodeStart, firstOffset, committedSize, candidateSize)))
                break;

            // Absorb commonPrefixArr range [prevRight+1, currRight] into crossEntryLcp once
            // we have at least two committed seps to compare. childCount here is the count
            // BEFORE this child commits — so childCount >= 2 means a prior sep exists.
            if (childCount >= 2)
            {
                int prevRight = level[childIdx + childCount - 1].FirstEntry;
                int currRight = curr.FirstEntry;
                for (int j = prevRight + 1; j <= currRight; j++)
                {
                    byte v = commonPrefixArr[j];
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
        scoped ReadOnlySpan<HsstIndexNodeInfo> children,
        int crossEntryLcp,
        scoped Span<byte> valueScratch,
        byte[] commonPrefixArr,
        scoped ref NativeMemoryListRef<byte> leafFirstKeys)
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
            sepLengths[i] = Math.Min(commonPrefixArr[rightIdx] + 1, _keyLength);
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

        // Pass 2: rightKey sourced from leafFirstKeys (no data-section IO) + AddKey.
        // Sep 0's rightKey also feeds commonPrefix. The planner's keySlotSize
        // (post-widen, post-strip) drives slice width.
        Span<byte> commonPrefixBuf = stackalloc byte[prefixLen];
        ReadOnlySpan<byte> leafKeys = leafFirstKeys.AsSpan();

        // keyBuf must fit the widest per-entry payload across layouts (see WriteLeafIndexNode).
        int perEntryKeyBytes = entryCount > 0 ? Math.Max(keySlotSize, _keyLength - prefixLen) : 0;
        int keyBufSize = entryCount * (2 + perEntryKeyBytes);
        Span<byte> keyBuf = stackalloc byte[keyBufSize];

        Span<byte> valueScratchSlice = valueScratch[..(entryCount * (2 + valueSlotSize))];

        if (entryCount > 0)
        {
            ReadOnlySpan<byte> rightKey = leafKeys.Slice(children[1].FirstLeafIdx * _keyLength, _keyLength);
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
            ReadOnlySpan<byte> rightKey = leafKeys.Slice(children[1].FirstLeafIdx * _keyLength, _keyLength);
            WriteUInt64LE(valueBuf, children[1].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(rightKey.Slice(prefixLen, KeySliceLength(prefixLen, keyType, keySlotSize, sepLengths[0])), valueBuf[..valueSlotSize]);
        }
        for (int i = 1; i < entryCount; i++)
        {
            ReadOnlySpan<byte> rightKey = leafKeys.Slice(children[i + 1].FirstLeafIdx * _keyLength, _keyLength);
            WriteUInt64LE(valueBuf, children[i + 1].ChildOffset - baseOffset, valueSlotSize);
            indexWriter.AddKey(rightKey.Slice(prefixLen, KeySliceLength(prefixLen, keyType, keySlotSize, sepLengths[i])), valueBuf[..valueSlotSize]);
        }
        indexWriter.FinalizeNode();
    }

    /// <summary>
    /// One-pass pre-computation of per-entry <c>LCP(prev, curr)</c> — the common prefix
    /// length of each entry's key against the prior entry's key. Writes into
    /// <paramref name="commonPrefixArr"/> (one byte per entry — fits because LCP is bounded
    /// by min(prev.Length, curr.Length) ≤ <see cref="MaxKeyLen"/> = 255). Consumers
    /// derive the natural separator length as <c>min(cp + 1, currKeyLen)</c>.
    /// </summary>
    private void PrecomputeCommonPrefixLengths(byte[] commonPrefixArr)
    {
        int n = _entryPositions.Length;
        Span<byte> prevKey = stackalloc byte[MaxKeyLen];
        Span<byte> currKey = stackalloc byte[MaxKeyLen];
        int prevKeyLen = 0;
        for (int i = 0; i < n; i++)
        {
            int currKeyLen = ReadKey(i, currKey);
            int cp = CommonPrefixLength(prevKey[..prevKeyLen], currKey[..currKeyLen]);
            commonPrefixArr[i] = (byte)cp;
            currKey[..currKeyLen].CopyTo(prevKey);
            prevKeyLen = currKeyLen;
        }
    }

    /// <summary>
    /// Read the full key for entry index <paramref name="idx"/> into <paramref name="dest"/>.
    /// In key-after-value mode walks the LEB128 ValueLength header byte-by-byte then reads
    /// the key. In key-first mode the entry position already points at FullKey byte 0, so
    /// the key bytes are read directly. Key length is uniform per HSST and stored in the
    /// trailer, not per entry. Returns the key length (≤ 255).
    /// </summary>
    private int ReadKey(int idx, scoped Span<byte> dest)
    {
        long pos = _entryPositions[idx];

        long offset = pos;
        if (!_keyFirst)
        {
            // Skip LEB128 ValueLength (the entry position aims at the LEB128 byte).
            Span<byte> oneByte = stackalloc byte[1];
            do
            {
                if (!_reader.TryRead(offset, oneByte)) ThrowReadFailed();
                offset++;
            } while ((oneByte[0] & 0x80) != 0);
        }

        int keyLen = _keyLength;
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

    private static void ThrowReadFailed()
        => throw new IOException("HSST data-section read out of range during index build.");

    // Conservative upper bound on BSearchIndexWriter header bytes: 12 base
    // (Flags + KeyCount u16 + KeySize u16 + ValueSize u8 + BaseOffset 6) + 1
    // optional CommonPrefixLen byte + a small slack.
    private const int NodeHeaderUpperBound = 16;

    // Conservative upper bound on an intermediate node's serialised size. The
    // phantom leftmost slot is dropped, so a node holding <paramref name="count"/>
    // children emits count-1 keys and count-1 values. The per-entry term
    // (2 + valueSlotSize) intentionally over-allocates by 2 bytes per value:
    // Uniform values on disk are just valueSlotSize bytes each (no length prefix),
    // but the +2 absorbs Variable-section length-table overhead and rounding
    // slack so the bound stays above the actual size for every layout the
    // planner picks. sumSepBytes upper-bounds the keys section the same way
    // (it sums count sep lengths against the count-1 actually emitted).
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

}

/// <summary>
/// Streaming top-down leaf-boundary splitter for HSST index builds. Borrows the LCP
/// min-segment tree and the DFS work stack from the caller's
/// <see cref="HsstBTreeBuilderBuffers"/> — the arrays are sized on demand in the
/// constructor and stay rented across builds for reuse. Caller pattern is
/// <c>using LeafBoundaryEnumerator iter = new(...)</c> then <c>while (iter.MoveNext()) ...</c>;
/// each <see cref="MoveNext"/> call drains the DFS until it can emit a (possibly merged)
/// leaf, captures it in <see cref="Current"/>, and returns <c>true</c>.
/// </summary>
/// <remarks>
/// Per-range decision in <see cref="TryGetNextRawSplit"/> (mirrors the prior
/// <c>PlanLeafBoundaries</c> in <see cref="HsstIndexBuilder{TWriter,TReader,TPin}"/>):
/// <list type="bullet">
/// <item><description><c>count ≤ minLeafEntries</c> — base case, emit.</description></item>
/// <item><description><c>count &gt; maxLeafEntries</c> — forced split; only the pivot scan
/// runs (the quality-gate maxLcp/value-range tracking would be unused).</description></item>
/// <item><description>Otherwise — full pass computes <c>maxLcp</c>, the two pivot
/// candidates, and entry-position min/max. Emit unless any of these encoding-quality
/// gates fires: <c>maxLcp − minLcp &gt; 4</c>, <c>maxLcp − minLcp == 3</c>,
/// <c>maxVal − minVal &gt; 2²⁴</c>, or the estimated node size (header +
/// <c>count · (keySlot + valueSlot)</c>) exceeds <see cref="MaxLeafBytes"/>.</description></item>
/// </list>
/// Pivot rule: rightmost position in <c>[lo+1, lo + count/2]</c> with <c>LCP == minLcp</c>,
/// with a leftmost-in-second-half fallback. Push right-half then left-half so the LIFO
/// stack pops them in left-to-right order and leaves emit sorted.
///
/// <para>On top of the raw splitter, <see cref="MoveNext"/> runs a streaming buffer-and-merge
/// pass: each raw split is tried against the most recently buffered (possibly already-merged)
/// split via <see cref="TryMergeIntoBuffer"/>. Two adjacent splits coalesce iff their individual
/// <see cref="BSearchIndexLayoutPlanner"/> outputs (<c>keyType</c>, <c>keySlotSize</c>,
/// <c>commonKeyPrefixLen</c>, <c>keyLittleEndian</c>) and value-slot widths match, the bridging
/// LCP (<c>commonPrefixArr[nextStart]</c>) is at least the buffered prefix length, the merged
/// entry count stays within <c>maxLeafEntries</c>, the merged value range still fits the same
/// value-slot width, and the estimated merged byte size stays within <see cref="MaxLeafBytes"/>.
/// The bridging-LCP requirement guarantees that next-side entries share enough leading bytes
/// with buffer entry 0 for the buffered common prefix to still be a valid prefix of every
/// merged-leaf entry; downstream the writer re-plans on the merged data and may pick a tighter
/// layout, but never a looser one, so the size estimate above remains an upper bound.</para>
/// </remarks>
internal ref struct LeafBoundaryEnumerator
{
    private readonly byte[] _lcp;
    private readonly ReadOnlySpan<long> _entryPositions;
    private readonly int _minLeafEntries;
    private readonly int _maxLeafEntries;
    private readonly int _keyLength;
    private readonly int _segTreeBase;

    // SegTree / DfsStack live on the buffers struct; these locals are aliases set in
    // the constructor for the duration of the enumeration. Returned-to-pool only when
    // the caller disposes the buffers struct itself.
    private readonly byte[] _segTree;
    private readonly int[] _stack;
    private int _sp;

    // Buffered split state. Empty buffer ⇒ _bufCount == 0.
    private int _bufStart;
    private int _bufCount;

    // Buffered planner output (cached so we can compare against the next split's
    // plan without re-running PlanFromProfile on the buffered range).
    private int _bufKeyType;
    private int _bufKeySlotSize;
    private int _bufPrefixLen;
    private bool _bufKeyLittleEndian;

    // Buffered value-range state.
    private long _bufMinVal;
    private long _bufMaxVal;
    private int _bufValueSlotSize;

    /// <summary>Number of <c>(lo, hi)</c> pairs of pending pending depth × branching that
    /// the DFS stack must accommodate. 1024 pairs is far above the practical peak
    /// (balanced binary partitioning gives O(log n) depth — under 100 for any realistic
    /// HSST) and the bounds check in <see cref="MoveNext"/> turns overflow into a clear
    /// exception rather than memory corruption.</summary>
    private const int StackCapacityInts = 4096;

    /// <summary>Estimated leaf-node bytes above which the splitter forces a further split,
    /// independent of separator/value gates. Matches
    /// <see cref="HsstBTreeOptions.DefaultMaxIntermediateBytes"/> so leaves and intermediate
    /// nodes share the same byte budget.</summary>
    private const int MaxLeafBytes = 2048;

    /// <summary>Header bytes assumed when estimating the serialized size of a leaf node —
    /// matches <c>HsstIndexBuilder.NodeHeaderUpperBound</c>: 12 base fields + 1 optional
    /// CommonPrefixLen byte + small slack.</summary>
    private const int LeafNodeHeaderOverheadBytes = 16;

    public int Current { get; private set; }

    public LeafBoundaryEnumerator(
        byte[] commonPrefixArr,
        ReadOnlySpan<long> entryPositions,
        int n,
        int minLeafEntries,
        int maxLeafEntries,
        int keyLength,
        scoped ref HsstBTreeBuilderBuffers buffers)
    {
        _lcp = commonPrefixArr;
        _entryPositions = entryPositions;
        _minLeafEntries = minLeafEntries;
        _maxLeafEntries = maxLeafEntries;
        _keyLength = keyLength;
        Current = 0;
        _bufCount = 0;

        // Min-segment tree over commonPrefixArr. Leaves at [base..base+n); tail filled
        // with byte.MaxValue so queries past entry n don't pull the min down.
        int b = 1;
        while (b < n) b <<= 1;
        _segTreeBase = b;
        HsstBTreeBuilderBuffers.EnsureSize(ref buffers.SegTree, Math.Max(2, b * 2));
        byte[] tree = buffers.SegTree!;
        _segTree = tree;
        for (int i = 0; i < n; i++) tree[b + i] = commonPrefixArr[i];
        for (int i = b + n; i < b * 2; i++) tree[i] = byte.MaxValue;
        for (int i = b - 1; i >= 1; i--)
        {
            byte a = tree[i * 2];
            byte c = tree[i * 2 + 1];
            tree[i] = a < c ? a : c;
        }

        // DFS stack, seeded with the full range. Stack length is fixed (StackCapacityInts);
        // after the first build the existing rental is reused without reallocation.
        HsstBTreeBuilderBuffers.EnsureSize(ref buffers.DfsStack, StackCapacityInts);
        int[] stack = buffers.DfsStack!;
        _stack = stack;
        _sp = 0;
        if (n > 0)
        {
            stack[_sp++] = 0;
            stack[_sp++] = n - 1;
        }
    }

    /// <summary>
    /// Drains raw splits from the inner DFS through the merge buffer, emitting one
    /// (possibly coalesced) leaf per call. Each call either:
    /// <list type="bullet">
    /// <item><description>flushes the current buffer because the next raw split won't merge into it
    /// (then re-seeds the buffer with that next split and returns), or</description></item>
    /// <item><description>reaches end-of-DFS and flushes the trailing buffer one last time, or</description></item>
    /// <item><description>returns <c>false</c> when both the DFS and the buffer are empty.</description></item>
    /// </list>
    /// </summary>
    public bool MoveNext(long pageOff)
    {
        // Carry-over buffer from a prior MoveNext call (the reseed after a failed
        // merge) was sized against that call's pageOff. The writer has since advanced
        // by the previously-flushed leaf, so the new pageOff may put the carry-over
        // across a 4 KiB boundary that the original gate never saw. Requeue its range
        // onto the DFS so the splitter can sub-split it against the up-to-date
        // pageOff. Skip when the buffer is already at minLeafEntries — splitter would
        // immediately re-emit the same range and we'd loop; fall through to the
        // fallback (allow cross).
        if (_bufCount > _minLeafEntries && (pageOff + EstimateBufSize() > PageLayout.PageSize))
        {
            if (_sp + 2 > _stack.Length)
                throw new InvalidOperationException(
                    "HSST leaf-splitter DFS stack exceeded — pathological key distribution.");
            _stack[_sp++] = _bufStart;
            _stack[_sp++] = _bufStart + _bufCount - 1;
            _bufCount = 0;
        }

        while (TryGetNextRawSplit(pageOff, out int rawStart, out int rawCount))
        {
            if (_bufCount == 0)
            {
                InitBuffer(rawStart, rawCount);
                continue;
            }

            if (TryMergeIntoBuffer(pageOff, rawStart, rawCount)) continue;

            // Flush buffer; replace with the new split.
            Current = _bufCount;
            InitBuffer(rawStart, rawCount);
            return true;
        }

        if (_bufCount > 0)
        {
            Current = _bufCount;
            _bufCount = 0;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Underlying DFS body — pops one frame per call until a raw split is ready to
    /// emit. Splits-or-pushes-halves logic is unchanged from the prior single-method
    /// implementation; the only difference is that the start index <c>lo</c> is now
    /// surfaced so the merge pass can probe entry-level state (LCPs, value positions)
    /// without re-deriving it from a running cumulative counter.
    /// </summary>
    private bool TryGetNextRawSplit(long pageOff, out int rawStart, out int rawCount)
    {
        const long ValueRangeLimit = 1L << 24;

        byte[] lcp = _lcp;
        int[] stack = _stack;
        ReadOnlySpan<long> entryPos = _entryPositions;
        int minLeafEntries = _minLeafEntries;
        int maxLeafEntries = _maxLeafEntries;

        while (_sp > 0)
        {
            int hi = stack[--_sp];
            int lo = stack[--_sp];
            int count = hi - lo + 1;

            if (count <= minLeafEntries)
            {
                rawStart = lo;
                rawCount = count;
                return true;
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
                // (half, hi]), and min / max of entry positions for the value-range gate.
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

                // Node-size estimate. Post-strip Uniform key slot ≈ gap + 1 (the widest
                // entry's natural sep len minus the leaf-wide common prefix); value slot is
                // MinBytesFor(valueRange) inlined. With the gap and value-range gates
                // bounding both factors, count · (keySlot + valueSlot) + header is a tight
                // upper bound on the actual leaf bytes — bigger than 2 KiB and we split.
                int gap = maxLcp - minLcp;
                long vr = maxVal - minVal;
                int valueSlot = vr == 0 ? 1 : (BitOperations.Log2((ulong)vr) >> 3) + 1;
                int estimatedSize = LeafNodeHeaderOverheadBytes + count * (gap + 1 + valueSlot);

                // Page-fit gate: if the leaf would straddle a 4 KiB page from the
                // writer's current offset, force a split — but only while count is
                // still above minLeafEntries, so a single oversized leaf at the
                // minimum count is allowed to cross (fallback policy).
                //
                // estimatedSize omits the planner's common-prefix overhead (CPL
                // byte is already in LeafNodeHeaderOverheadBytes but the prefix
                // bytes themselves are not). Without compensating, this gate would
                // let a leaf cross by up to prefixLen bytes. prefixLen is bounded
                // by min(minLcp + 1, keyLength) — adding that as a per-leaf upper
                // bound matches what BSearchIndexWriter and the merger actually
                // account for.
                int prefixOverheadUB = Math.Min(minLcp + 1, _keyLength);
                // Split when the post-strip slot would land outside the SIMD-friendly
                // widths {1, 2, 4, 8} — gap+1 is the post-strip slot upper bound, so
                // gap > 4 covers slots 6+ (no SIMD fast path even after planner widening,
                // since widening to 8 is only possible when budget ≥ 8). gap ∈ {0,1,2,3}
                // lands the planner on slot ∈ {1,2,2,4} (with widening), all SIMD-served.
                bool splitNeeded =
                    gap > 4 ||
                    vr > ValueRangeLimit ||
                    estimatedSize > MaxLeafBytes ||
                    (pageOff + estimatedSize + prefixOverheadUB > PageLayout.PageSize && count > minLeafEntries);
                if (!splitNeeded)
                {
                    rawStart = lo;
                    rawCount = count;
                    return true;
                }
            }
            else
            {
                // Forced split — the quality gate result is unused; skip the maxLcp /
                // value-range tracking and scan only for the pivot. Hot path for ranges
                // above maxLeafEntries; doing the full pass would be wasteful.
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

            if (_sp + 4 > stack.Length)
                throw new InvalidOperationException(
                    "HSST leaf-splitter DFS stack exceeded — pathological key distribution.");

            stack[_sp++] = split;
            stack[_sp++] = hi;
            stack[_sp++] = lo;
            stack[_sp++] = split - 1;
        }

        rawStart = 0;
        rawCount = 0;
        return false;
    }

    /// <summary>
    /// Seed the merge buffer from a fresh raw split: derive the planner profile
    /// from <c>commonPrefixArr</c>, call
    /// <see cref="BSearchIndexLayoutPlanner.PlanFromProfile"/>, compute the value
    /// range, and cache the plan + value-slot fields on <c>_buf*</c>.
    /// </summary>
    private void InitBuffer(int start, int count)
    {
        ComputeSplitPlan(start, count,
            out int keyType, out int keySlotSize, out int prefixLen, out bool keyLittleEndian,
            out long minVal, out long maxVal, out int valueSlotSize);

        _bufStart = start;
        _bufCount = count;
        _bufKeyType = keyType;
        _bufKeySlotSize = keySlotSize;
        _bufPrefixLen = prefixLen;
        _bufKeyLittleEndian = keyLittleEndian;
        _bufMinVal = minVal;
        _bufMaxVal = maxVal;
        _bufValueSlotSize = valueSlotSize;
    }

    /// <summary>
    /// Probe whether the raw split at <c>[nextStart, nextStart + nextCount)</c> can be
    /// coalesced into the buffered split. A merge succeeds iff:
    /// <list type="number">
    /// <item><description><c>_bufCount + nextCount ≤ _maxLeafEntries</c> — splitter's hard cap.</description></item>
    /// <item><description>The next split's planner output matches the buffer's exactly
    /// (<c>keyType</c>, <c>keySlotSize</c>, <c>commonKeyPrefixLen</c>, <c>keyLittleEndian</c>).</description></item>
    /// <item><description>The bridging LCP <c>commonPrefixArr[nextStart]</c> ≥ the buffered
    /// prefix length, guaranteeing the prefix *bytes* still align across the cut so
    /// stripping is still valid.</description></item>
    /// <item><description>The next split's value-slot equals the buffer's, and the merged
    /// value range still fits that same slot.</description></item>
    /// <item><description>The estimated merged byte size, using the buffered plan, stays
    /// within <see cref="MaxLeafBytes"/>.</description></item>
    /// </list>
    /// The merged leaf is encoded by <see cref="HsstIndexBuilder{TWriter,TReader,TPin}.WriteLeafIndexNode"/>,
    /// which re-Plans on the merged data — it may pick a tighter prefix (smaller leaf)
    /// than the buffered plan suggested, but never a looser one given the bridging-LCP
    /// guarantee, so the size-estimate upper bound holds.
    /// </summary>
    private bool TryMergeIntoBuffer(long pageOff, int nextStart, int nextCount)
    {
        int mergedCount = _bufCount + nextCount;
        if (mergedCount > _maxLeafEntries) return false;

        // Bridging LCP between buf's last key and next's first key. When this is
        // < _bufPrefixLen the merged leaf can't safely use the buffered prefix
        // (some of next's entries don't share enough leading bytes with buf's
        // entry 0), so the merge is unsafe regardless of next's own plan.
        int bridgeLcp = _lcp[nextStart];
        if (bridgeLcp < _bufPrefixLen) return false;

        ComputeSplitPlan(nextStart, nextCount,
            out int nextKeyType, out int nextKeySlotSize, out int nextPrefixLen, out bool nextKeyLittleEndian,
            out long nextMinVal, out long nextMaxVal, out int nextValueSlotSize);

        if (nextKeyType != _bufKeyType ||
            nextKeySlotSize != _bufKeySlotSize ||
            nextPrefixLen != _bufPrefixLen ||
            nextKeyLittleEndian != _bufKeyLittleEndian ||
            nextValueSlotSize != _bufValueSlotSize)
        {
            return false;
        }

        // Merged value-slot. Mirrors WriteLeafIndexNode's baseOffset+valueSlotSize formula.
        long mergedMinVal = Math.Min(_bufMinVal, nextMinVal);
        long mergedMaxVal = Math.Max(_bufMaxVal, nextMaxVal);
        long mergedBaseOffset = 0;
        if (mergedCount > 1 && mergedMinVal > 0 && mergedMinVal < mergedMaxVal) mergedBaseOffset = mergedMinVal;
        long mergedRange = mergedMaxVal - mergedBaseOffset;
        int mergedValueSlotSize = mergedRange == 0 ? 1 : (BitOperations.Log2((ulong)mergedRange) >> 3) + 1;

        if (mergedValueSlotSize != _bufValueSlotSize) return false;

        // Byte-size budget. Use the buffered plan as the upper bound: the writer's
        // re-Plan on merged data can only shrink the leaf (longer prefix, smaller
        // slot), never grow it, given the bridging-LCP guarantee above. For
        // Variable layout (keyType=0) we'd need per-entry length to estimate but
        // this branch is unreachable here because the merge predicate requires
        // matching keyType / keySlotSize, and the planner only picks Variable for
        // effMaxLen > 8 (where keySlotSize == 0); _bufKeySlotSize == 0 would fail
        // the equality check against any next that's non-Variable. Treat
        // keyType=0 conservatively by using a generous per-entry cost.
        int perEntryKeyBytes = _bufKeyType == 0 ? _keyLength + 2 : _bufKeySlotSize;
        int prefixOverhead = _bufPrefixLen > 0 ? 1 + _bufPrefixLen : 0;
        int estimated = LeafNodeHeaderOverheadBytes + prefixOverhead +
                        mergedCount * (perEntryKeyBytes + _bufValueSlotSize);
        if (estimated > MaxLeafBytes) return false;

        // Page-fit gate (companion to TryGetNextRawSplit's): if absorbing the next
        // raw split would push the buffered leaf across a 4 KiB page boundary from
        // the writer's current offset, refuse the merge so the buffered leaf is
        // flushed standalone and the next split starts a fresh buffer.
        if (pageOff + estimated > PageLayout.PageSize) return false;

        // Commit.
        _bufCount = mergedCount;
        _bufMinVal = mergedMinVal;
        _bufMaxVal = mergedMaxVal;
        // Plan/value-slot fields unchanged (verified equal above).
        return true;
    }

    /// <summary>
    /// Upper-bound estimate of the buffered leaf's serialized size, using the cached
    /// planner profile (<c>_bufKeyType</c>, <c>_bufKeySlotSize</c>, <c>_bufPrefixLen</c>,
    /// <c>_bufValueSlotSize</c>). Mirrors <see cref="TryMergeIntoBuffer"/>'s estimator so
    /// the page-fit gate at <see cref="MoveNext"/>'s carry-over check matches what the
    /// merger would have used. Conservative for Variable layout (keyType=0): assumes the
    /// widest per-entry payload, matching the comment in TryMergeIntoBuffer.
    /// </summary>
    private readonly int EstimateBufSize()
    {
        int perEntryKeyBytes = _bufKeyType == 0 ? _keyLength + 2 : _bufKeySlotSize;
        int prefixOverhead = _bufPrefixLen > 0 ? 1 + _bufPrefixLen : 0;
        return LeafNodeHeaderOverheadBytes + prefixOverhead +
               _bufCount * (perEntryKeyBytes + _bufValueSlotSize);
    }

    /// <summary>
    /// One-pass computation of the planner profile + value range for the range
    /// <c>[start, start+count)</c>, followed by a single call to
    /// <see cref="BSearchIndexLayoutPlanner.PlanFromProfile"/>. Mirrors the planner-input
    /// derivation that <c>HsstIndexBuilder.WriteLeafIndexNode</c> does (sepLengths from
    /// <c>commonPrefixArr</c>, value range from <c>_entryPositions</c>) so the merger
    /// and the writer agree on what the per-split plan would be.
    /// </summary>
    private void ComputeSplitPlan(
        int start, int count,
        out int keyType, out int keySlotSize, out int prefixLen, out bool keyLittleEndian,
        out long minVal, out long maxVal, out int valueSlotSize)
    {
        byte[] lcp = _lcp;
        ReadOnlySpan<long> entryPos = _entryPositions;
        int keyLength = _keyLength;

        int firstLen = Math.Min(lcp[start] + 1, keyLength);
        int minLen = firstLen;
        int maxLen = firstLen;
        bool allSameLen = true;
        int secondLen = -1;
        bool allSameLenExceptFirst = count >= 2;
        // ComputeCrossEntryLcpLeaf convention: singleton ⇒ MaxKeyLen (255) so the
        // planner's `min(crossEntryLcp, minLen)` short-circuits to minLen.
        int crossEntryLcp = 255;

        minVal = entryPos[start];
        maxVal = minVal;

        for (int i = 1; i < count; i++)
        {
            byte cp = lcp[start + i];
            if (cp < crossEntryLcp) crossEntryLcp = cp;
            int len = Math.Min(cp + 1, keyLength);
            if (len < minLen) minLen = len;
            if (len > maxLen) maxLen = len;
            if (len != firstLen) allSameLen = false;
            if (i == 1) secondLen = len;
            else if (len != secondLen) allSameLenExceptFirst = false;

            long ep = entryPos[start + i];
            if (ep < minVal) minVal = ep;
            if (ep > maxVal) maxVal = ep;
        }

        BSearchIndexLayoutPlanner.PlanFromProfile(
            count, firstLen, secondLen, minLen, maxLen, allSameLen, allSameLenExceptFirst,
            crossEntryLcp, keyLength,
            out prefixLen, out keyType, out keySlotSize, out keyLittleEndian);

        long baseOffset = 0;
        if (count > 1 && minVal > 0 && minVal < maxVal) baseOffset = minVal;
        long range = maxVal - baseOffset;
        valueSlotSize = range == 0 ? 1 : (BitOperations.Log2((ulong)range) >> 3) + 1;
    }

    /// <summary>
    /// Min over the underlying LCP array in inclusive range <c>[l, r]</c>, answered via the
    /// segment tree in O(log n). Iterative bottom-up walk: absorb the left fringe when
    /// <c>l</c> is a right child, absorb the right fringe when <c>r</c> is a left child,
    /// then ascend.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RangeMinLcp(int l, int r)
    {
        byte[] tree = _segTree;
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

    public void Dispose()
    {
        // SegTree and DfsStack are owned by the caller's HsstBTreeBuilderBuffers — they
        // stay rented until that struct itself is disposed.
    }
}
