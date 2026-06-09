// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Builds a hashtable-accelerated <see cref="IndexType.BTreeKeyFirst"/> (0x07) /
/// <see cref="IndexType.BTree"/> (0x01) HSST: a table split into partitions, each an ordinary
/// data + index region followed by a 64-byte-aligned 8-way hashtable. Entries MUST be added in
/// sorted key order; the builder closes a partition once its accumulated key bytes reach
/// <see cref="HsstBTreeOptions.PartitionThresholdBytes"/> (this bounds the per-partition hashtable
/// build buffer).
/// </summary>
/// <remarks>
/// Each partition's hashtable + metadata is emitted as a <see cref="BTreeNodeKind.Hashtable"/> node
/// (<c>[Flag][27-byte record][InnerRootPrefixLen u8][InnerRootPrefix]</c>). The node bytes are
/// buffered as partitions close, then written together just before a trailing <b>directory</b>
/// B-tree whose leaf-level children point at those nodes (via
/// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}.RecordNodeChild"/>); a single partition skips
/// the directory and is the root node itself. So the whole blob is an ordinary B-tree the standard
/// reader walks — directory → Hashtable node → (probe; on miss/floor) the partition's inner B-tree
/// — with no special index type, and the blob's <i>trailer format is the standard one</i>
/// (<c>[RootPrefix][RootPrefixLen u8][RootSize u16][KeyLength u8][IndexType u8]</c>). Everything
/// shares one byte-0-relative coordinate system (<c>baseOffsetOverride =</c> this blob's byte 0). A
/// single partition with fewer than <see cref="HsstBTreeOptions.HashtableMinKeys"/> keys degrades to
/// a plain B-tree (no node), byte-identical to a standalone build. See FORMAT.md.
/// </remarks>
public ref struct HsstPartitionedBTreeBuilder<TWriter, TReader, TPin>
    where TWriter : IByteBufferWriterWithReader<TReader, TPin>
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    private ref TWriter _writer;
    private readonly ref HsstPartitionedBTreeBuilderBuffers _buffers;
    private readonly HsstBTreeOptions _options;
    private readonly int _keyLength;
    private readonly long _hsstBase;
    private readonly bool _keyFirst;

    private HsstBTreeBuilder<TWriter, TReader, TPin> _inner;
    private bool _partitionOpen;
    private bool _needFirstKey;
    private bool _solePlainFinalized;
    private long _partitionStartAbs;
    private long _accumKeyBytes;

    /// <param name="keyLength">Fixed key length (0–255) for every entry and every directory key.</param>
    /// <param name="keyFirst">
    /// Entry layout of the partition data: key-first (<c>[Flag][Key][LEB128][Value]</c>, for the slot
    /// levels — use <see cref="Add"/>) or key-after-value (<c>[Value][Flag][LEB128][Key]</c>, for the
    /// per-address column whose value sizes are unknown up front — use
    /// <see cref="BeginValueWrite"/>/<see cref="FinishValueWrite"/> streaming). The blob's trailer
    /// IndexType reflects the inner entry layout: 0x07 (key-first) vs 0x01 (key-after-value).
    /// </param>
    public HsstPartitionedBTreeBuilder(ref TWriter writer, ref HsstPartitionedBTreeBuilderBuffers buffers, int keyLength, HsstBTreeOptions? options = null, bool keyFirst = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyLength, 255);
        _writer = ref writer;
        _hsstBase = _writer.Written;
        buffers.ResetForBuild();
        _buffers = ref buffers;
        _options = options ?? HsstBTreeOptions.Default;
        _keyLength = keyLength;
        _keyFirst = keyFirst;
        _partitionOpen = false;
        _needFirstKey = false;
        _solePlainFinalized = false;
        _partitionStartAbs = 0;
        _accumKeyBytes = 0;
        _inner = default;
    }

    /// <summary>No-op; the caller owns and disposes the <see cref="HsstPartitionedBTreeBuilderBuffers"/>.</summary>
    public void Dispose() { }

    /// <summary>Add a key-value entry (value known up front). Keys must be exactly the declared key length and strictly ascending.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (key.Length != _keyLength)
            throw new ArgumentException($"key length {key.Length} != declared keyLength {_keyLength}", nameof(key));
        if (!_partitionOpen) OpenPartition();
        _inner.Add(key, value, out long entryStart);
        RecordEntry(key, entryStart);
    }

    /// <summary>
    /// Streaming value write for key-after-value mode (the value length need not be known up front).
    /// Returns the writer to stream the value into; close the entry with
    /// <see cref="FinishValueWrite"/>. Not valid in key-first mode (use <see cref="Add"/>).
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        if (_keyFirst) throw new InvalidOperationException("Key-first partitioned builder requires Add(key, value); streaming is not supported.");
        if (!_partitionOpen) OpenPartition();
        return ref _inner.BeginValueWrite();
    }

    /// <summary>Close a streamed entry started with <see cref="BeginValueWrite"/>.</summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key, long valueLength)
    {
        if (key.Length != _keyLength)
            throw new ArgumentException($"key length {key.Length} != declared keyLength {_keyLength}", nameof(key));
        _inner.FinishValueWrite(key, valueLength, out long entryStart);
        RecordEntry(key, entryStart);
    }

    /// <summary>
    /// Per-entry bookkeeping shared by <see cref="Add"/> and <see cref="FinishValueWrite"/>: records
    /// the partition's first key (lazily — the key is only known here for streamed entries), pushes
    /// the (hash, entry-offset) pair for the hashtable, and closes the partition once the key buffer
    /// reaches the threshold (a mid-stream close ⇒ the blob is partitioning, so the partition always
    /// gets a hashtable).
    /// </summary>
    private void RecordEntry(scoped ReadOnlySpan<byte> key, long entryStart)
    {
        if (_needFirstKey)
        {
            _buffers.DirKeys.AddRange(key);
            _needFirstKey = false;
        }
        _buffers.AccumHashes.Add(HsstPartitionHashtable.Hash(key));
        _buffers.AccumOffsets.Add(entryStart);
        _accumKeyBytes += key.Length;

        long span = _writer.Written - _partitionStartAbs;
        if (_accumKeyBytes >= _options.PartitionThresholdBytes || span >= _options.PartitionMaxSpanBytes)
            ClosePartition(closedByBuild: false);
    }

    /// <summary>
    /// Close the final partition (if open), then emit the buffered Hashtable nodes and the trailer.
    /// A single hashtable-less partition was already finalized as a plain B-tree by
    /// <see cref="ClosePartition"/>. A single hashtabled partition writes its Hashtable node as the
    /// root. Otherwise the nodes are written bunched and a directory B-tree (built over them as leaf
    /// children) is the root. The blob's trailer is the standard one — the standard reader walks it.
    /// </summary>
    public void Build()
    {
        if (_partitionOpen) ClosePartition(closedByBuild: true);
        if (_solePlainFinalized) return;

        int partitionCount = _buffers.DirValueLengths.Count;
        IndexType indexType = _keyFirst ? IndexType.BTreeKeyFirst : IndexType.BTree;

        ReadOnlySpan<byte> nodeBytes = _buffers.DirValues.AsSpan();
        ReadOnlySpan<int> nodeLengths = _buffers.DirValueLengths.AsSpan();

        // Single hashtabled partition: the lone Hashtable node is the blob root.
        if (partitionCount == 1)
        {
            int len = nodeLengths[0];
            nodeBytes[..len].CopyTo(_writer.GetSpan(len));
            _writer.Advance(len);
            WriteHashtableRootTrailer(len, indexType);
            return;
        }

        // Multi-partition: emit the Hashtable nodes contiguously (bunched, just before the directory
        // — keeps them cache-local to the directory walk) and build a directory B-tree whose leaf
        // children point at those nodes.
        ReadOnlySpan<byte> dirKeys = _buffers.DirKeys.AsSpan();
        // fullSeparators: the directory indexes per-partition key RANGES, so separators must be the
        // partitions' full first keys (an LCP-truncated separator can be ≤ a partition's max key and
        // misroute lookups that cross a prefix boundary within a partition).
        HsstBTreeBuilder<TWriter, TReader, TPin> dir = new(
            ref _writer, ref _buffers.Inner, _keyLength, _options, keyFirst: true, baseOffsetOverride: _hsstBase, fullSeparators: true);
        try
        {
            int off = 0;
            for (int p = 0; p < partitionCount; p++)
            {
                long nodeOffset = _writer.Written - _hsstBase;
                int len = nodeLengths[p];
                nodeBytes.Slice(off, len).CopyTo(_writer.GetSpan(len));
                _writer.Advance(len);
                off += len;
                dir.RecordNodeChild(nodeOffset, dirKeys.Slice(p * _keyLength, _keyLength));
            }
            dir.Build(indexType);
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>
    /// Append the standard 5-byte trailer (<c>RootPrefixLen = 0</c>) for a single-partition blob
    /// whose root is the lone Hashtable node (just written). The node carries its own inner-root
    /// prefix, so no prefix rides the trailer; <c>RootSize = nodeSize</c> locates the node at
    /// <c>HSST end − 5 − nodeSize</c>.
    /// </summary>
    private void WriteHashtableRootTrailer(int nodeSize, IndexType indexType)
    {
        Span<byte> tail = _writer.GetSpan(5);
        tail[0] = 0; // RootPrefixLen
        tail[1] = (byte)nodeSize;
        tail[2] = (byte)(nodeSize >> 8);
        tail[3] = (byte)_keyLength;
        tail[4] = (byte)indexType;
        _writer.Advance(5);
    }

    // Opens a partition without its first key — the first key is recorded by RecordEntry on the
    // first entry (it is only known at FinishValueWrite time for streamed entries).
    private void OpenPartition()
    {
        _partitionStartAbs = _writer.Written;
        _inner = new HsstBTreeBuilder<TWriter, TReader, TPin>(
            ref _writer, ref _buffers.Inner, _keyLength, _options, keyFirst: _keyFirst, baseOffsetOverride: _hsstBase);
        _accumKeyBytes = 0;
        _partitionOpen = true;
        _needFirstKey = true;
    }

    /// <param name="closedByBuild">
    /// True when called for the final open partition at <see cref="Build"/> (end of input), false
    /// when a mid-stream threshold in <see cref="RecordEntry"/> forces a split. The <b>sole</b>
    /// partition (closed by Build with no prior partitions) with fewer than
    /// <see cref="HsstBTreeOptions.HashtableMinKeys"/> keys is emitted as a plain standalone B-tree
    /// (trailer and all, byte-identical to a non-partitioned build) and no partition/hashtable is
    /// produced. Every other closed partition gets a hashtable, so once a blob partitions, a
    /// directory never holds a hashtable-less partition.
    /// </param>
    private void ClosePartition(bool closedByBuild)
    {
        int keyCount = _buffers.AccumHashes.Count;
        bool sole = closedByBuild && _buffers.DirValueLengths.Count == 0;

        if (sole && keyCount < _options.HashtableMinKeys)
        {
            // Whole blob is one small partition: emit it as a plain standalone B-tree — its inner
            // index already has byte-0-relative offsets (base == this partition's start), so its own
            // trailer makes it byte-identical to building it without the partitioned wrapper.
            _inner.Build();
            _inner.Dispose();
            _solePlainFinalized = true;
            _partitionOpen = false;
            ClearAccum();
            return;
        }

        Span<byte> prefixScratch = stackalloc byte[256];
        _inner.BuildIndexOnly(out long innerRootOffset, out _, out int innerRootPrefixLen, prefixScratch);
        long innerBufferEnd = _writer.Written - _hsstBase; // byte-0-relative end of the inner index region
        long dataRegionStart = _partitionStartAbs - _hsstBase; // byte-0-relative start of this partition's data section
        _inner.Dispose();

        PadTo64();
        long hashtableOffset = _writer.Written - _hsstBase;
        int bucketCount = HsstPartitionHashtable.BucketCountFor(keyCount);
        int regionSize = checked((int)HsstPartitionHashtable.RegionSize(bucketCount));
        Span<byte> buckets = _writer.GetSpan(regionSize);
        buckets[..regionSize].Clear();

        ReadOnlySpan<ulong> hashes = _buffers.AccumHashes.AsSpan();
        ReadOnlySpan<long> offsets = _buffers.AccumOffsets.AsSpan();
        for (int i = 0; i < keyCount; i++)
        {
            // Forward distance from the data-section start to the entry — bounded by the data
            // section size (< 256 TiB by the span split) so it fits u48.
            long forward = offsets[i] - dataRegionStart;
            HsstPartitionHashtable.TryInsert(buckets, bucketCount, hashes[i], forward);
        }
        _writer.Advance(regionSize);

        BufferHashtableNode(innerRootOffset, innerBufferEnd, hashtableOffset, dataRegionStart, bucketCount, innerRootPrefixLen, prefixScratch[..innerRootPrefixLen]);

        _partitionOpen = false;
        ClearAccum();
    }

    private void ClearAccum()
    {
        _buffers.AccumHashes.Clear();
        _buffers.AccumOffsets.Clear();
        _accumKeyBytes = 0;
    }

    /// <summary>Pad the writer to the next 64-byte boundary (absolute), so the hashtable's buckets are cache-line aligned.</summary>
    private void PadTo64()
    {
        int pad = (int)((-_writer.Written) & (HsstPartitionHashtable.BucketBytes - 1));
        if (pad == 0) return;
        _writer.GetSpan(pad)[..pad].Clear();
        _writer.Advance(pad);
    }

    /// <summary>
    /// Buffer the partition's Hashtable-node bytes
    /// (<c>[Flag=Hashtable][27-byte record][InnerRootPrefixLen u8][InnerRootPrefix]</c>) into
    /// <c>DirValues</c>; they are emitted contiguously at <see cref="Build"/> time. Buffering (rather
    /// than writing inline after each partition) keeps every node adjacent to the directory.
    /// </summary>
    private void BufferHashtableNode(long innerRootOffset, long innerBufferEnd, long hashtableOffset, long dataRegionStart, int bucketCount, int innerRootPrefixLen, scoped ReadOnlySpan<byte> innerRootPrefix)
    {
        int nodeLen = 1 + HsstPartitionHashtable.NodeRecordFixedSize + 1 + innerRootPrefixLen;
        Span<byte> head = stackalloc byte[1 + HsstPartitionHashtable.NodeRecordFixedSize + 1 + 255];
        head = head[..nodeLen];
        head[0] = (byte)BTreeNodeKind.Hashtable;
        Span<byte> rec = head[1..];
        HsstPartitionHashtable.WriteU48(rec, innerRootOffset);
        HsstPartitionHashtable.WriteU48(rec[6..], innerBufferEnd);
        HsstPartitionHashtable.WriteU48(rec[12..], hashtableOffset);
        HsstPartitionHashtable.WriteU48(rec[18..], dataRegionStart);
        HsstPartitionHashtable.WriteU24(rec[24..], bucketCount);
        rec[HsstPartitionHashtable.NodeRecordFixedSize] = (byte)innerRootPrefixLen;
        if (innerRootPrefixLen > 0) innerRootPrefix.CopyTo(rec[(HsstPartitionHashtable.NodeRecordFixedSize + 1)..]);
        _buffers.DirValues.AddRange(head);
        _buffers.DirValueLengths.Add(nodeLen);
    }
}
