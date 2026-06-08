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

    // Most-recently-closed partition's inner-root descriptor, used by the single-partition
    // fast path in Build() to emit a plain 0x07 trailer (byte-identical to a non-partitioned
    // key-first build) so small contracts pay no directory/hashtable overhead.
    private long _lastRootOffset;
    private int _lastRootSize;
    private int _lastRootPrefixLen;
    private bool _lastHadHashtable;

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
        // either trigger fires. The span guard is the correctness bound (u32 hashtable offset).
        long span = _writer.Written - _partitionStartAbs;
        if (_accumKeyBytes >= _options.PartitionThresholdBytes || span >= _options.PartitionMaxSpanBytes)
            ClosePartition();
    }

    /// <summary>
    /// Close the final partition (if open), then either emit the directory B-tree + 0x08
    /// trailer, or — when the whole blob collapsed to a single hashtable-less partition —
    /// append a plain 0x07 trailer so the output is byte-identical to a non-partitioned
    /// key-first build (zero overhead for small contracts).
    /// </summary>
    public void Build()
    {
        if (_partitionOpen) ClosePartition();

        // Single-partition fast path: the lone partition's inner index is already a complete
        // 0x07 data + index region (byte-0-relative offsets == partition-relative for the
        // first partition); just append its 0x07 trailer instead of a directory layer.
        if (_buffers.DirValueLengths.Count == 1 && !_lastHadHashtable)
        {
            WriteSingleBTreeKeyFirstTrailer();
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

    private void OpenPartition(scoped ReadOnlySpan<byte> firstKey)
    {
        _partitionStartAbs = _writer.Written;
        _inner = new HsstBTreeBuilder<TWriter, TReader, TPin>(
            ref _writer, ref _buffers.Inner, _keyLength, _options, keyFirst: true, baseOffsetOverride: _hsstBase);
        _buffers.DirKeys.AddRange(firstKey);
        _accumKeyBytes = 0;
        _partitionOpen = true;
    }

    private void ClosePartition()
    {
        Span<byte> rootPrefix = stackalloc byte[256];
        int rootPrefixLen = _inner.BuildIndexOnly(out long innerRootOffset, out int innerRootSize, rootPrefix);
        long innerScopeEnd = _writer.Written - _hsstBase; // byte-0-relative end of the inner index region
        _inner.Dispose();

        int keyCount = _buffers.AccumHashes.Count;
        long hashtableOffset = 0;
        int bucketCountLog2 = 0;
        if (_accumKeyBytes > _options.HashtableMinBytes && keyCount > 0)
        {
            PadTo64();
            hashtableOffset = _writer.Written - _hsstBase;
            bucketCountLog2 = HsstPartitionHashtable.BucketCountLog2For(keyCount);
            int regionSize = checked((int)HsstPartitionHashtable.RegionSize(bucketCountLog2));
            Span<byte> buckets = _writer.GetSpan(regionSize);
            buckets[..regionSize].Clear();

            ReadOnlySpan<ulong> hashes = _buffers.AccumHashes.AsSpan();
            ReadOnlySpan<long> offsets = _buffers.AccumOffsets.AsSpan();
            for (int i = 0; i < keyCount; i++)
            {
                // Both offsets are byte-0-relative; their difference is the backward distance
                // from the hashtable start to the entry, < partition span < 2 GiB ⇒ fits u32.
                long backward = hashtableOffset - offsets[i];
                HsstPartitionHashtable.TryInsert(buckets, bucketCountLog2, hashes[i], backward);
            }
            _writer.Advance(regionSize);
        }

        EncodeDirValue(innerRootOffset, innerScopeEnd, hashtableOffset, bucketCountLog2, rootPrefix[..rootPrefixLen]);

        // Stash this partition's root descriptor for the single-partition fast path in Build().
        _lastRootOffset = innerRootOffset;
        _lastRootSize = innerRootSize;
        _lastRootPrefixLen = rootPrefixLen;
        rootPrefix[..rootPrefixLen].CopyTo(_buffers.RootPrefixScratch);
        _lastHadHashtable = bucketCountLog2 > 0;

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

    private void EncodeDirValue(long innerRootOffset, long innerScopeEnd, long hashtableOffset, int bucketCountLog2, scoped ReadOnlySpan<byte> rootPrefix)
    {
        Span<byte> rec = stackalloc byte[HsstPartitionHashtable.DirRecordFixedSize];
        WriteU48(rec, innerRootOffset);
        WriteU48(rec[6..], innerScopeEnd);
        WriteU48(rec[12..], hashtableOffset);
        rec[18] = (byte)bucketCountLog2;
        rec[19] = (byte)rootPrefix.Length;
        _buffers.DirValues.AddRange(rec);
        if (rootPrefix.Length > 0) _buffers.DirValues.AddRange(rootPrefix);
        _buffers.DirValueLengths.Add(HsstPartitionHashtable.DirRecordFixedSize + rootPrefix.Length);
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
}
