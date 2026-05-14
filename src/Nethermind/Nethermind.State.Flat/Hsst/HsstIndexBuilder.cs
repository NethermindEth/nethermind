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
    // Pointer to the caller-supplied buffers struct holding the work arrays/lists
    // (CommonPrefixArr, LeafFirstKeys, CurrentLevel, NextLevel, ValueScratch, SegTree,
    // DfsStack). Stored as void* because HsstBTreeBuilderBuffers is a ref struct and
    // therefore not eligible for ordinary T* / managed-pointer fields.
    private readonly unsafe void* _buffersPtr;

    public unsafe HsstIndexBuilder(ref TWriter writer, TReader reader, ReadOnlySpan<long> entryPositions, int keyLength, scoped ref HsstBTreeBuilderBuffers buffers)
    {
        _writer = ref writer;
        _reader = reader;
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
            commonPrefixArr, _entryPositions, n, minLeafEntries, maxLeafEntries, ref bufs);

        int entryIdx = 0;
        int leafIdx = 0;

        // True until the first node of the index region has been written.
        // Used to gate MaybePadToNextPage so we never pad after the root —
        // the trailer formula assumes [...root...][trailer] with no gap.
        bool firstNode = true;

        while (iter.MoveNext())
        {
            int count = iter.Current;

            // Pad to a fresh page if we're within PageAlignPadThreshold of
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
        // keySlotSize bytes, Variable/UniformWithLen take the per-entry natural sep
        // length (up to _keyLength - prefixLen). Use the max so all paths fit.
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
    /// Walks the LEB128 ValueLength header byte-by-byte (so end-of-data-section reads
    /// stay in bounds), then reads the key bytes — key length is uniform per HSST and
    /// stored in the trailer, not per entry. Returns the key length (≤ 255).
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
    /// <c>root_start = HSST_end - 4 - rootSize</c> assumes the trailer abuts the
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

}

/// <summary>
/// Streaming top-down leaf-boundary splitter for HSST index builds. Borrows the LCP
/// min-segment tree and the DFS work stack from the caller's
/// <see cref="HsstBTreeBuilderBuffers"/> — the arrays are sized on demand in the
/// constructor and stay rented across builds for reuse. Caller pattern is
/// <c>using LeafBoundaryEnumerator iter = new(...)</c> then <c>while (iter.MoveNext()) ...</c>;
/// each <see cref="MoveNext"/> call runs the DFS loop body until a leaf size would
/// emit, captures it in <see cref="Current"/>, and returns <c>true</c>.
///
/// Per-range decision (mirrors the prior <c>PlanLeafBoundaries</c> in
/// <see cref="HsstIndexBuilder{TWriter,TReader,TPin}"/>):
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
///
/// Pivot rule: rightmost position in <c>[lo+1, lo + count/2]</c> with <c>LCP == minLcp</c>,
/// with a leftmost-in-second-half fallback. Push right-half then left-half so the LIFO
/// stack pops them in left-to-right order and leaves emit sorted.
/// </summary>
file ref struct LeafBoundaryEnumerator
{
    private readonly byte[] _lcp;
    private readonly ReadOnlySpan<long> _entryPositions;
    private readonly int _minLeafEntries;
    private readonly int _maxLeafEntries;
    private readonly int _segTreeBase;

    // SegTree / DfsStack live on the buffers struct; these locals are aliases set in
    // the constructor for the duration of the enumeration. Returned-to-pool only when
    // the caller disposes the buffers struct itself.
    private readonly byte[] _segTree;
    private readonly int[] _stack;
    private int _sp;

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
        scoped ref HsstBTreeBuilderBuffers buffers)
    {
        _lcp = commonPrefixArr;
        _entryPositions = entryPositions;
        _minLeafEntries = minLeafEntries;
        _maxLeafEntries = maxLeafEntries;
        Current = 0;

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

    public bool MoveNext()
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
                Current = count;
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

                bool splitNeeded =
                    gap > 4 ||
                    gap == 3 ||
                    vr > ValueRangeLimit ||
                    estimatedSize > MaxLeafBytes;
                if (!splitNeeded)
                {
                    Current = count;
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
        return false;
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
