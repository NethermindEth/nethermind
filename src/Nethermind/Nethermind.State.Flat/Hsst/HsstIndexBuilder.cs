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
    // (PendingKeys, EntryPositions, CommonPrefixArr, CurrentLevel, NextLevel, ValueScratch).
    // Stored as void* because HsstBTreeBuilderBuffers is a ref struct and therefore not
    // eligible for ordinary T* / managed-pointer fields.
    private readonly unsafe void* _buffersPtr;

    // Global entry index of the first key still in PendingKeys. ReadKey treats any
    // <c>idx &gt;= _pendingFirstEntryIdx</c> as living in PendingKeys at local offset
    // <c>(idx - _pendingFirstEntryIdx) * keyLength</c>; lower indices fall through to
    // <see cref="ReadKeyFromDataSection"/>. The EmitInlineLeaf transient builder
    // passes the current pending start; the Build-time builder passes
    // <c>entryPositions.Length</c> so the pending branch is never taken.
    private readonly int _pendingFirstEntryIdx;
    // Data-section reader view used for <see cref="ReadKeyFromDataSection"/>. Default
    // <c>(TReader)default</c> when this builder only ever reads from PendingKeys
    // (the inline-emit path).
    private TReader _reader;
    private readonly bool _useDataReader;

    public unsafe HsstIndexBuilder(ref TWriter writer, ReadOnlySpan<long> entryPositions, int keyLength, scoped ref HsstBTreeBuilderBuffers buffers, bool keyFirst = false, int pendingFirstEntryIdx = 0, TReader reader = default!, bool useDataReader = false)
    {
        _writer = ref writer;
        _entryPositions = entryPositions;
        _keyLength = keyLength;
        _keyFirst = keyFirst;
        _pendingFirstEntryIdx = pendingFirstEntryIdx;
        _reader = reader;
        _useDataReader = useDataReader;
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
            // Empty index: write a single empty leaf node.
            return WriteEmptyLeafIndexNode();
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
        ref NativeMemoryListRef<HsstIndexNodeInfo> currentNative = ref bufs.CurrentLevel;
        ref NativeMemoryListRef<HsstIndexNodeInfo> nextNative = ref bufs.NextLevel;
        nextNative.Clear();

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
            return checked((int)(absoluteIndexStart - only.ChildOffset));
        }

        bool firstNode = true;

        // Build internal levels until single root.
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
                    commonPrefixArr,
                    out int crossEntryLcp);
                ReadOnlySpan<HsstIndexNodeInfo> children = current.Slice(childIdx, childCount);

                // First intermediate of the index region: skip the leading pad so we
                // don't insert a hole between the last page-local leaf (data region)
                // and the first intermediate. From the second intermediate onward,
                // pad to a fresh page if we're close to the boundary.
                if (!firstNode) MaybePadToNextPage();
                firstNode = false;

                long nodeStart = _writer.Written;
                long relativeStart = nodeStart - startWritten;
                WriteIndexNode(children, BSearchNodeKind.Intermediate,
                    valueScratchArr, commonPrefixArr, out int intermediatePrefixLen);
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

                childIdx += childCount;
            }

            // Swap roles for the next level — ref reassignment, no struct copy.
            ref NativeMemoryListRef<HsstIndexNodeInfo> tmp = ref currentNative;
            currentNative = ref nextNative;
            nextNative = ref tmp;
        }

        _rootPrefixLen = lastNodePrefixLen;
        return lastNodeLen;
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
    /// so its first <see cref="RootPrefixLen"/> bytes are the root's CommonKeyPrefix.
    /// </summary>
    public unsafe int CopyRootPrefixBytes(scoped Span<byte> dest)
    {
        if (_rootPrefixLen == 0) return 0;
        // Re-read entry 0's first _rootPrefixLen bytes from the data section. By the
        // time Build() has finished, every entry has been folded into a leaf and
        // PendingKeys is empty, so the data section is the only place left to find
        // the key bytes. One read per build.
        Span<byte> keyScratch = stackalloc byte[MaxKeyLen];
        ReadKeyFromDataSection(0, keyScratch[.._keyLength]);
        keyScratch[.._rootPrefixLen].CopyTo(dest);
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

    private int WriteEmptyLeafIndexNode()
    {
        long nodeStart = _writer.Written;
        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            NodeKind = BSearchNodeKind.Leaf,
            KeyType = 0,
            BaseOffset = 0,
            KeySlotSize = 1,
            // Empty leaf has no values; ValueSlotSize = 2 is the smallest supported width
            // and the size that gets encoded into the Flags byte. The values section is
            // 0 bytes either way (KeyCount * ValueSize = 0 * 2 = 0).
            ValueSlotSize = 2,
        }, default, default);
        indexWriter.FinalizeNode();
        return checked((int)(_writer.Written - nodeStart));
    }

    /// <summary>
    /// Unified node writer: emit a BSearchIndex node of the requested
    /// <see cref="BSearchNodeKind"/> covering the given <paramref name="children"/>. Used
    /// for both inline page-local leaves (each child wraps a single entry; pushed from
    /// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/> trigger paths) and intermediate
    /// nodes (each child is a previously-emitted leaf / intermediate). The per-child
    /// separator length is <c>max(natural LCP + 1, children[i].PrefixLen)</c>: short
    /// separators are widened so the parent's slot always carries every byte of the
    /// child's planner-picked CommonKeyPrefix. The planner then picks this node's own
    /// <c>CommonPrefixLen</c> from the shared per-entry LCP array
    /// (<paramref name="commonPrefixArr"/>) capped at <c>minLen</c> over the sepLengths.
    /// The result is returned via <paramref name="nodePrefixLen"/> so the caller can
    /// record it on the descriptor it pushes for the next level up.
    /// </summary>
    internal void WriteIndexNode(
        scoped ReadOnlySpan<HsstIndexNodeInfo> children,
        BSearchNodeKind kind,
        scoped Span<byte> valueScratch,
        byte[] commonPrefixArr,
        out int nodePrefixLen)
    {
        int count = children.Length;

        // Per-child separator length: natural LCP-derived length widened to at least
        // the child's own planner-picked prefix so the parent slot can hand the child
        // every byte of its CommonKeyPrefix at descent time.
        Span<int> sepLengths = stackalloc int[count];
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

        Span<byte> currKey = stackalloc byte[MaxKeyLen];
        Span<byte> commonPrefixBuf = stackalloc byte[prefixLen];
        if (prefixLen > 0)
        {
            ReadKey(children[0].FirstEntry, currKey);
            currKey[..prefixLen].CopyTo(commonPrefixBuf);
        }

        int perEntryKeyBytes = Math.Max(keySlotSize, _keyLength - prefixLen);
        int keyBufSize = count * (2 + Math.Max(1, perEntryKeyBytes));
        Span<byte> keyBuf = stackalloc byte[keyBufSize];
        Span<byte> valueScratchSlice = valueScratch[..(count * (2 + valueSlotSize))];

        scoped BSearchIndexWriter<TWriter> indexWriter = new(ref _writer, new BSearchIndexMetadata
        {
            NodeKind = kind,
            KeyType = keyType,
            BaseOffset = (ulong)baseOffset,
            KeySlotSize = keySlotSize,
            ValueSlotSize = valueSlotSize,
            IsKeyLittleEndian = keyLittleEndian,
        }, keyBuf, valueScratchSlice, commonPrefixBuf);

        Span<byte> valueBuf = stackalloc byte[8];

        for (int i = 0; i < count; i++)
        {
            ReadKey(children[i].FirstEntry, currKey);
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
        scoped ReadOnlySpan<HsstIndexNodeInfo> level, int childIdx,
        int maxChildren, int byteThreshold,
        int minChildren, int minBytes,
        long nodeStart, long firstOffset,
        byte[] commonPrefixArr,
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

        // Phantom slot 0 is in play: children[childIdx]'s separator is emitted with
        // length children[childIdx].PrefixLen so the parent's separator carries every
        // byte of the child's own common prefix. Seed sumSepBytes / maxSepLen / commonLen
        // from that, and seed firstSep with children[childIdx]'s firstKey[..PrefixLen].
        HsstIndexNodeInfo firstChild = level[childIdx];
        int firstSepLen = firstChild.PrefixLen;
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
        // running LCP is meaningful from childCount == 1 onward.
        int commonLen = firstSepLen;
        Span<byte> firstSep = stackalloc byte[MaxKeyLen];
        Span<byte> sepBuf = stackalloc byte[MaxKeyLen];
        Span<byte> firstKeyScratch = stackalloc byte[MaxKeyLen];
        Span<byte> rightKeyScratch = stackalloc byte[MaxKeyLen];
        if (firstSepLen > 0)
        {
            ReadKey(firstChild.FirstEntry, firstKeyScratch[.._keyLength]);
            firstKeyScratch[..firstSepLen].CopyTo(firstSep);
        }

        while (childCount < hardMax)
        {
            HsstIndexNodeInfo curr = level[childIdx + childCount];
            // Adjacency invariant: prev.LastEntry == curr.FirstEntry - 1, so
            // commonPrefixArr[curr.FirstEntry] is exactly LCP(leftKey, rightKey).
            // Natural separator length is min(LCP + 1, _keyLength); the actual stored
            // length is widened to at least curr.PrefixLen so the parent's separator
            // carries every byte of the child's prefix at descent time.
            ReadKey(curr.FirstEntry, rightKeyScratch[.._keyLength]);
            int naturalSep = Math.Min(commonPrefixArr[curr.FirstEntry] + 1, _keyLength);
            int sepLen = Math.Max(naturalSep, curr.PrefixLen);
            rightKeyScratch[..sepLen].CopyTo(sepBuf);

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
            int newEffSepLen = newMaxSepLen - newCommonLen;
            int candidateSize = IntermediateNodeSizeUpperBound(newCount, newSumSep, valueSlotSize);
            int committedSize = IntermediateNodeSizeUpperBound(childCount, sumSepBytes, committedValueSlot);
            if (childCount >= minChildren &&
                committedSize >= minBytes &&
                (newEffSepLen > 4 ||
                 WouldCrossNewPage(nodeStart, firstOffset, committedSize, candidateSize)))
                break;

            // Absorb commonPrefixArr range [prevRight+1, currRight] into crossEntryLcp once
            // we have at least one committed sep to compare against. With phantom slot 0
            // restored the first committed child already has a separator, so the fire
            // condition drops from childCount >= 2 to childCount >= 1.
            if (childCount >= 1)
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
            commonLen = newCommonLen;
        }
        return childCount;
    }

    // WriteInternalIndexNode and PrecomputeCommonPrefixLengths have been folded into
    // <see cref="WriteIndexNode"/> and the online LCP path in HsstBTreeBuilder.OnEntryAdded
    // respectively. The intermediate-construction loop now calls WriteIndexNode with
    // <c>BSearchNodeKind.Intermediate</c>, and the leaf-emission path in HsstBTreeBuilder
    // calls it with <c>BSearchNodeKind.Leaf</c> after wrapping each pending entry in a
    // single-entry HsstIndexNodeInfo descriptor.

    /// <summary>
    /// Read the full key for entry index <paramref name="idx"/> into <paramref name="dest"/>.
    /// Dispatches by where the key lives at this point in the build:
    /// <list type="bullet">
    /// <item><description>
    ///   <c>idx &gt;= _pendingFirstEntryIdx</c> — the entry is in the in-flight pending set;
    ///   its key sits in <c>Buffers.PendingKeys</c> at local offset
    ///   <c>(idx - _pendingFirstEntryIdx) * keyLength</c>. Used by the inline page-local
    ///   leaf emit path.
    /// </description></item>
    /// <item><description>
    ///   <c>idx &lt; _pendingFirstEntryIdx</c> — the entry has already been folded into
    ///   an inline leaf; <c>PendingKeys</c> no longer holds it, so we re-read the full
    ///   key from the data section via <see cref="ReadKeyFromDataSection"/>. Used by
    ///   the Build-time intermediate-construction path.
    /// </description></item>
    /// </list>
    /// Returns the key length (≤ 255).
    /// </summary>
    private int ReadKey(int idx, scoped Span<byte> dest)
    {
        int keyLen = _keyLength;
        if (keyLen <= 0) return 0;
        if (idx >= _pendingFirstEntryIdx)
        {
            ReadOnlySpan<byte> pending = Buffers.PendingKeys.AsSpan();
            int localOffset = (idx - _pendingFirstEntryIdx) * keyLen;
            pending.Slice(localOffset, keyLen).CopyTo(dest);
        }
        else
        {
            ReadKeyFromDataSection(idx, dest[..keyLen]);
        }
        return keyLen;
    }

    /// <summary>
    /// Read entry <paramref name="idx"/>'s full key by reaching into the data section
    /// via <see cref="_reader"/>. For key-after-value entries
    /// (<c>[Value][FlagByte][LEB128 ValueLength][FullKey]</c>) walks past the leading
    /// flag byte and the LEB128 byte(s) to locate the key. For key-first entries
    /// (<c>[FlagByte][FullKey][LEB128 ValueLength][Value]</c>) skips just the leading
    /// flag byte. Throws if the reader view isn't valid (the inline-emit transient
    /// builder never takes this path — all its reads land in PendingKeys).
    /// </summary>
    private void ReadKeyFromDataSection(int idx, scoped Span<byte> dest)
    {
        if (!_useDataReader)
            throw new InvalidOperationException("HsstIndexBuilder asked to read entry " + idx + " from the data section but no reader view was supplied at construction.");

        long pos = _entryPositions[idx] + 1; // skip the leading flag byte
        if (!_keyFirst)
        {
            // Skip LEB128 ValueLength. 1-10 bytes, continuation-bit terminator on bit 7.
            Span<byte> oneByte = stackalloc byte[1];
            do
            {
                if (!_reader.TryRead(pos, oneByte)) ThrowReadFailed();
                pos++;
            } while ((oneByte[0] & 0x80) != 0);
        }
        if (!_reader.TryRead(pos, dest)) ThrowReadFailed();
    }

    private static void ThrowReadFailed() =>
        throw new IOException("HSST data-section read out of range during index build.");

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
