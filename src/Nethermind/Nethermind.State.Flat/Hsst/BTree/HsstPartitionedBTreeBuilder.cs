// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Builds an <see cref="IndexType.PartitionedBTreeKeyFirst"/> (0x08) HSST: a key-first
/// table split into partitions, each an ordinary key-first data + index region optionally
/// followed by a 64-byte-aligned 8-way hashtable, plus a trailing directory B-tree mapping
/// partition-first-keys to partition metadata. Entries MUST be added in sorted key order
/// (one call per key); the builder closes a partition once its accumulated key bytes reach
/// <see cref="HsstBTreeOptions.PartitionThresholdBytes"/> or its on-disk span approaches
/// <see cref="HsstBTreeOptions.PartitionMaxSpanBytes"/>.
/// </summary>
/// <remarks>
/// Composition over modification: each partition and the directory are produced by an
/// ordinary <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/> (key-first) driven over the
/// same writer with <c>baseOffsetOverride = </c> this blob's byte 0, so every recorded
/// offset shares one byte-0-relative coordinate system that a reader walks with a single
/// whole-blob bound. The per-partition hashtable stores, per key, the backward distance from
/// the hashtable start to the entry; a reader probes one bucket and, on a miss, falls back to
/// the partition's inner B-tree (located via the directory metadata). See FORMAT.md.
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

    private HsstBTreeBuilder<TWriter, TReader, TPin> _inner;
    private bool _partitionOpen;
    private long _partitionStartAbs;
    private long _accumKeyBytes;

    // Most-recently-closed partition's descriptor, used by the single-partition fast paths in
    // Build(): a plain 0x07 trailer when it has no hashtable (byte-identical to a non-partitioned
    // key-first build), or a 0x09 trailer carrying the hashtable metadata when it does — either
    // way skipping the directory B-tree.
    private long _lastRootOffset;
    private int _lastRootSize;
    private int _lastRootPrefixLen;
    private bool _lastHadHashtable;
    private long _lastInnerScopeEnd;
    private long _lastHashtableOffset;
    private int _lastBucketCount;
    private long _lastDataRegionStart;

    /// <param name="keyLength">Fixed key length (0–255) for every entry and every directory key.</param>
    public HsstPartitionedBTreeBuilder(ref TWriter writer, ref HsstPartitionedBTreeBuilderBuffers buffers, int keyLength, HsstBTreeOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyLength, 255);
        _writer = ref writer;
        _hsstBase = _writer.Written;
        buffers.ResetForBuild();
        _buffers = ref buffers;
        _options = options ?? HsstBTreeOptions.Default;
        _keyLength = keyLength;
        _partitionOpen = false;
        _partitionStartAbs = 0;
        _accumKeyBytes = 0;
        _lastRootOffset = 0;
        _lastRootSize = 0;
        _lastRootPrefixLen = 0;
        _lastHadHashtable = false;
        _lastInnerScopeEnd = 0;
        _lastHashtableOffset = 0;
        _lastBucketCount = 0;
        _lastDataRegionStart = 0;
        _inner = default;
    }

    /// <summary>No-op; the caller owns and disposes the <see cref="HsstPartitionedBTreeBuilderBuffers"/>.</summary>
    public void Dispose() { }

    /// <summary>Add a key-value entry. Keys must be exactly the declared key length and strictly ascending.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (key.Length != _keyLength)
            throw new ArgumentException($"key length {key.Length} != declared keyLength {_keyLength}", nameof(key));

        if (!_partitionOpen) OpenPartition(key);

        _inner.Add(key, value, out long entryStart);
        _buffers.AccumHashes.Add(HsstPartitionHashtable.Hash(key));
        _buffers.AccumOffsets.Add(entryStart);
        _accumKeyBytes += key.Length;

        // Every Add is a valid split point (one self-contained entry), so close as soon as
        // either trigger fires. The span guard is the correctness bound (u24 forward offset).
        // A mid-stream close means the blob is partitioning, so the partition always gets a
        // hashtable (closedByBuild: false).
        long span = _writer.Written - _partitionStartAbs;
        if (_accumKeyBytes >= _options.PartitionThresholdBytes || span >= _options.PartitionMaxSpanBytes)
            ClosePartition(closedByBuild: false);
    }

    /// <summary>
    /// Close the final partition (if open), then emit one of three trailers depending on how
    /// the blob collapsed:
    /// <list type="bullet">
    /// <item>single partition, no hashtable → a plain <see cref="IndexType.BTreeKeyFirst"/>
    /// (0x07) trailer (byte-identical to a non-partitioned key-first build);</item>
    /// <item>single partition, with hashtable →
    /// <see cref="IndexType.SinglePartitionHashtableBTreeKeyFirst"/> (0x09) — the hashtable
    /// metadata in the trailer, no directory B-tree;</item>
    /// <item>multiple partitions → the directory B-tree +
    /// <see cref="IndexType.PartitionedBTreeKeyFirst"/> (0x08).</item>
    /// </list>
    /// </summary>
    public void Build()
    {
        if (_partitionOpen) ClosePartition(closedByBuild: true);

        // Single-partition fast paths: the lone partition's inner index is already a complete
        // byte-0-relative data + index region, so we skip the (useless, single-entry) directory.
        if (_buffers.DirValueLengths.Count == 1)
        {
            if (!_lastHadHashtable) WriteSingleBTreeKeyFirstTrailer();
            else WriteSinglePartitionHashtableTrailer();
            return;
        }

        HsstBTreeBuilder<TWriter, TReader, TPin> dir = new(
            ref _writer, ref _buffers.Inner, _keyLength, _options, keyFirst: true, baseOffsetOverride: _hsstBase);
        try
        {
            ReadOnlySpan<byte> dirKeys = _buffers.DirKeys.AsSpan();
            ReadOnlySpan<byte> dirValues = _buffers.DirValues.AsSpan();
            ReadOnlySpan<int> dirLengths = _buffers.DirValueLengths.AsSpan();
            int valOff = 0;
            for (int p = 0; p < dirLengths.Length; p++)
            {
                dir.Add(dirKeys.Slice(p * _keyLength, _keyLength), dirValues.Slice(valOff, dirLengths[p]));
                valOff += dirLengths[p];
            }
            dir.Build(IndexType.PartitionedBTreeKeyFirst);
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>
    /// Append a plain <see cref="IndexType.BTreeKeyFirst"/> (0x07) trailer for the single
    /// partition's inner index. The writer already sits at the inner index end (no hashtable
    /// was written), and the partition's offsets are byte-0-relative — which equals
    /// partition-relative for the first partition — so the result is byte-identical to what a
    /// standalone <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/> in key-first mode would
    /// have produced for these entries.
    /// </summary>
    private void WriteSingleBTreeKeyFirstTrailer()
    {
        int rootPrefixLen = _lastRootPrefixLen;
        int trailerLen = 5 + rootPrefixLen;
        Span<byte> tail = _writer.GetSpan(trailerLen);
        if (rootPrefixLen > 0) _buffers.RootPrefixScratch.AsSpan(0, rootPrefixLen).CopyTo(tail);
        tail[rootPrefixLen] = (byte)rootPrefixLen;
        tail[rootPrefixLen + 1] = (byte)_lastRootSize;
        tail[rootPrefixLen + 2] = (byte)(_lastRootSize >> 8);
        tail[rootPrefixLen + 3] = (byte)_keyLength;
        tail[rootPrefixLen + 4] = (byte)IndexType.BTreeKeyFirst;
        _writer.Advance(trailerLen);
    }

    /// <summary>
    /// Append a <see cref="IndexType.SinglePartitionHashtableBTreeKeyFirst"/> (0x09) trailer for
    /// the lone partition: the 20-byte hashtable metadata record straight in the trailer (no
    /// directory B-tree), laid out so a tail scan can locate it. Layout (low→high):
    /// <c>[InnerRootPrefix: prefixLen][Metadata: 28][KeyLength: u8][IndexType: u8]</c> — the
    /// prefix precedes the fixed record so the reader reads the record first (it carries
    /// prefixLen) and then the prefix bytes before it. The metadata is the same
    /// <see cref="HsstPartitionHashtable.DirRecordFixedSize"/>-byte record the directory would
    /// have held as the partition's value (its DataRegionStart is 0 — a single partition's data
    /// starts at the blob's byte 0).
    /// </summary>
    private void WriteSinglePartitionHashtableTrailer()
    {
        int prefixLen = _lastRootPrefixLen;
        int recSize = HsstPartitionHashtable.DirRecordFixedSize;
        int trailerLen = prefixLen + recSize + 2;
        Span<byte> tail = _writer.GetSpan(trailerLen);
        if (prefixLen > 0) _buffers.RootPrefixScratch.AsSpan(0, prefixLen).CopyTo(tail);
        Span<byte> rec = tail.Slice(prefixLen, recSize);
        WriteRecord(rec, _lastRootOffset, _lastInnerScopeEnd, _lastHashtableOffset, _lastDataRegionStart, _lastBucketCount, prefixLen);
        tail[prefixLen + recSize] = (byte)_keyLength;
        tail[prefixLen + recSize + 1] = (byte)IndexType.SinglePartitionHashtableBTreeKeyFirst;
        _writer.Advance(trailerLen);
    }

    private void OpenPartition(scoped ReadOnlySpan<byte> firstKey)
    {
        _partitionStartAbs = _writer.Written;
        _inner = new HsstBTreeBuilder<TWriter, TReader, TPin>(
            ref _writer, ref _buffers.Inner, _keyLength, _options, keyFirst: true, baseOffsetOverride: _hsstBase);
        _buffers.DirKeys.AddRange(firstKey);
        _accumKeyBytes = 0;
        _partitionOpen = true;
    }

    /// <param name="closedByBuild">
    /// True when called for the final open partition at <see cref="Build"/> (end of input),
    /// false when a mid-stream threshold in <see cref="Add"/> forces a split. A hashtable is
    /// built for every partition that has keys — the only exception is the **sole** partition
    /// (closed by Build with no prior partitions) when it is under <c>HashtableMinBytes</c>:
    /// that blob is emitted as plain 0x07 with no hashtable at all (no partition). Once a blob
    /// partitions, every partition is hashtabled, so a directory never holds a hashtable-less
    /// partition.
    /// </param>
    private void ClosePartition(bool closedByBuild)
    {
        Span<byte> rootPrefix = stackalloc byte[256];
        int rootPrefixLen = _inner.BuildIndexOnly(out long innerRootOffset, out int innerRootSize, rootPrefix);
        long innerScopeEnd = _writer.Written - _hsstBase; // byte-0-relative end of the inner index region
        long dataRegionStart = _partitionStartAbs - _hsstBase; // byte-0-relative start of this partition's data section
        _inner.Dispose();

        int keyCount = _buffers.AccumHashes.Count;
        long hashtableOffset = 0;
        int bucketCount = 0;
        bool sole = closedByBuild && _buffers.DirValueLengths.Count == 0; // the whole blob is this one partition
        bool wantHashtable = keyCount > 0 && (!sole || _accumKeyBytes > _options.HashtableMinBytes);
        if (wantHashtable)
        {
            PadTo64();
            hashtableOffset = _writer.Written - _hsstBase;
            bucketCount = HsstPartitionHashtable.BucketCountFor(keyCount);
            int regionSize = checked((int)HsstPartitionHashtable.RegionSize(bucketCount));
            Span<byte> buckets = _writer.GetSpan(regionSize);
            buckets[..regionSize].Clear();

            ReadOnlySpan<ulong> hashes = _buffers.AccumHashes.AsSpan();
            ReadOnlySpan<long> offsets = _buffers.AccumOffsets.AsSpan();
            for (int i = 0; i < keyCount; i++)
            {
                // Forward distance from the data-section start to the entry — bounded by the
                // data section size (< 16 MiB by the span split) so it fits u24. The inner
                // index sits after the data section and is not addressed here.
                long forward = offsets[i] - dataRegionStart;
                HsstPartitionHashtable.TryInsert(buckets, bucketCount, hashes[i], forward);
            }
            _writer.Advance(regionSize);
        }

        EncodeDirValue(innerRootOffset, innerScopeEnd, hashtableOffset, dataRegionStart, bucketCount, rootPrefix[..rootPrefixLen]);

        // Stash this partition's descriptor for the single-partition fast paths in Build().
        _lastRootOffset = innerRootOffset;
        _lastRootSize = innerRootSize;
        _lastRootPrefixLen = rootPrefixLen;
        rootPrefix[..rootPrefixLen].CopyTo(_buffers.RootPrefixScratch);
        _lastHadHashtable = bucketCount > 0;
        _lastInnerScopeEnd = innerScopeEnd;
        _lastHashtableOffset = hashtableOffset;
        _lastBucketCount = bucketCount;
        _lastDataRegionStart = dataRegionStart;

        _partitionOpen = false;
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

    private void EncodeDirValue(long innerRootOffset, long innerScopeEnd, long hashtableOffset, long dataRegionStart, int bucketCount, scoped ReadOnlySpan<byte> rootPrefix)
    {
        Span<byte> rec = stackalloc byte[HsstPartitionHashtable.DirRecordFixedSize];
        WriteRecord(rec, innerRootOffset, innerScopeEnd, hashtableOffset, dataRegionStart, bucketCount, rootPrefix.Length);
        _buffers.DirValues.AddRange(rec);
        if (rootPrefix.Length > 0) _buffers.DirValues.AddRange(rootPrefix);
        _buffers.DirValueLengths.Add(HsstPartitionHashtable.DirRecordFixedSize + rootPrefix.Length);
    }

    /// <summary>
    /// Write the 28-byte partition metadata record (shared by the directory value and the 0x09
    /// trailer): <c>[InnerRootOffset 6][InnerScopeEnd 6][HashtableOffset 6][DataRegionStart 6][HashtableBucketCount u24][InnerRootPrefixLen u8]</c>.
    /// </summary>
    private static void WriteRecord(Span<byte> rec, long innerRootOffset, long innerScopeEnd, long hashtableOffset, long dataRegionStart, int bucketCount, int rootPrefixLen)
    {
        WriteU48(rec, innerRootOffset);
        WriteU48(rec[6..], innerScopeEnd);
        WriteU48(rec[12..], hashtableOffset);
        WriteU48(rec[18..], dataRegionStart);
        WriteU24(rec[24..], bucketCount);
        rec[27] = (byte)rootPrefixLen;
    }

    private static void WriteU48(Span<byte> dest, long value)
    {
        dest[0] = (byte)value;
        dest[1] = (byte)(value >> 8);
        dest[2] = (byte)(value >> 16);
        dest[3] = (byte)(value >> 24);
        dest[4] = (byte)(value >> 32);
        dest[5] = (byte)(value >> 40);
    }

    private static void WriteU24(Span<byte> dest, int value)
    {
        dest[0] = (byte)value;
        dest[1] = (byte)(value >> 8);
        dest[2] = (byte)(value >> 16);
    }
}
