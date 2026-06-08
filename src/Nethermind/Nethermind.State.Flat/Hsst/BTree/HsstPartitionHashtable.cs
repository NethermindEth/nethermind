// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Shared on-disk layout, hash, and bucket/way codec for the per-partition hashtable of
/// the <see cref="IndexType.PartitionedBTreeKeyFirst"/> variant — the single source of
/// truth used by both <see cref="HsstPartitionedBTreeBuilder{TWriter,TReader,TPin}"/> and
/// <see cref="HsstPartitionedBTreeReader"/>.
/// </summary>
/// <remarks>
/// A hashtable is an array of <see cref="NumBuckets"/> cache-line buckets. Each bucket is
/// <see cref="BucketBytes"/> (64) bytes = <see cref="WaysPerBucket"/> (8) ways of
/// <see cref="WayBytes"/> (8) bytes; each way is <c>[Offset: u32 LE][Tag: u32 LE]</c>.
/// <c>Tag == 0</c> marks an empty way; live placements force the tag to be ≥ 1 and fill a
/// bucket from way 0 upward, so a reader may stop at the first empty way. <c>Offset</c> is
/// the entry's flag-byte position stored as the backward distance from the hashtable start
/// (<c>Offset = HashtableOffset − EntryOffset</c>), recovered as
/// <c>entry_abs = hashtable_abs − Offset</c>. The table is best-effort: a key whose bucket
/// already holds 8 live ways is dropped, and the reader falls back to the partition's
/// inner B-tree.
/// </remarks>
internal static class HsstPartitionHashtable
{
    internal const int BucketBytes = 64;
    internal const int WaysPerBucket = 8;
    internal const int WayBytes = 8;

    /// <summary>
    /// Fixed size of a directory metadata record (before the inner-root prefix bytes):
    /// <c>[InnerRootOffset: 6][InnerScopeEnd: 6][HashtableOffset: 6][HashtableBucketCountLog2: u8][InnerRootPrefixLen: u8]</c>.
    /// </summary>
    internal const int DirRecordFixedSize = 20;

    /// <summary>Hard cap on the bucket-count log2 (16M buckets ≈ 1 GiB) — a runaway guard;
    /// real partitions are bounded by the key-bytes / span thresholds well below this.</summary>
    internal const int MaxBucketCountLog2 = 24;

    /// <summary>Byte size of a hashtable with <paramref name="bucketCountLog2"/> buckets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long RegionSize(int bucketCountLog2) => (long)BucketBytes << bucketCountLog2;

    /// <summary>
    /// Pick the bucket-count log2 for a partition of <paramref name="keyCount"/> keys: the
    /// smallest power of two giving ≈ 8-way capacity at ~50% load (capacity = buckets·8,
    /// target ≈ 2·keyCount ⇒ buckets ≈ keyCount/4). Always ≥ 1 (so NumBuckets ≥ 2, keeping
    /// log2 = 0 free as the "no hashtable" sentinel), capped at <see cref="MaxBucketCountLog2"/>.
    /// </summary>
    internal static int BucketCountLog2For(int keyCount)
    {
        // >> 2 (not / 4) keeps this module free of integer division entirely — bucket
        // selection at lookup time is a power-of-two mask (see BucketIndex), and this
        // build-time sizing avoids div/mod too.
        int target = Math.Max(1, (keyCount + 3) >> 2);
        int log2 = Math.Max(1, BitOperations.Log2((uint)(target - 1)) + 1);
        return Math.Min(log2, MaxBucketCountLog2);
    }

    /// <summary>64-bit mixing hash of <paramref name="key"/>; the bucket index is its low
    /// bits and the way tag its high 32 bits. Identical on the write and read sides.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash(scoped ReadOnlySpan<byte> key)
    {
        const ulong prime1 = 0x9E3779B185EBCA87UL;
        const ulong prime2 = 0xC2B2AE3D27D4EB4FUL;
        ulong h = 0x27D4EB2F165667C5UL ^ ((ulong)key.Length * prime1);
        int i = 0;
        for (; i + 8 <= key.Length; i += 8)
        {
            ulong k = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice(i));
            k *= prime2;
            k = BitOperations.RotateLeft(k, 31);
            k *= prime1;
            h ^= k;
            h = BitOperations.RotateLeft(h, 27) * prime1 + 0x52DCE729UL;
        }
        ulong tail = 0;
        for (int shift = 0; i < key.Length; i++, shift += 8) tail |= (ulong)key[i] << shift;
        tail *= prime2;
        tail = BitOperations.RotateLeft(tail, 31);
        tail *= prime1;
        h ^= tail;
        // fmix64 finalizer for full avalanche across the bucket / tag split.
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }

    /// <summary>Bucket index for <paramref name="hash"/> in a table of <c>1 &lt;&lt; bucketCountLog2</c> buckets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int BucketIndex(ulong hash, int bucketCountLog2) => (int)(hash & ((1UL << bucketCountLog2) - 1));

    /// <summary>Way tag for <paramref name="hash"/> — the high 32 bits, forced to ≥ 1 so 0 stays the empty marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Tag(ulong hash)
    {
        uint tag = (uint)(hash >> 32);
        return tag == 0 ? 1u : tag;
    }

    /// <summary>
    /// Insert <paramref name="backwardOffset"/> (= HashtableOffset − EntryOffset, must fit
    /// u32) into <paramref name="buckets"/> for <paramref name="hash"/>. Returns false on
    /// bucket overflow (8 live ways) — the caller drops the key (best-effort).
    /// </summary>
    internal static bool TryInsert(Span<byte> buckets, int bucketCountLog2, ulong hash, long backwardOffset)
    {
        int bucket = BucketIndex(hash, bucketCountLog2);
        uint tag = Tag(hash);
        Span<byte> b = buckets.Slice(bucket * BucketBytes, BucketBytes);
        for (int way = 0; way < WaysPerBucket; way++)
        {
            Span<byte> slot = b.Slice(way * WayBytes, WayBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]) != 0) continue; // occupied
            BinaryPrimitives.WriteUInt32LittleEndian(slot, (uint)backwardOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], tag);
            return true;
        }
        return false;
    }

    /// <summary>Tag stored in way <paramref name="way"/> of a 64-byte <paramref name="bucket"/> (0 = empty).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint WayTag(scoped ReadOnlySpan<byte> bucket, int way) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bucket.Slice(way * WayBytes + 4, 4));

    /// <summary>Backward offset stored in way <paramref name="way"/> of a 64-byte <paramref name="bucket"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint WayOffset(scoped ReadOnlySpan<byte> bucket, int way) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bucket.Slice(way * WayBytes, 4));
}
