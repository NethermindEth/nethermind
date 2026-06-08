// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Shared on-disk layout, hash, and bucket/way codec for the per-partition hashtable of
/// the <see cref="IndexType.PartitionedBTreeKeyFirst"/> variant — the single source of
/// truth used by both <see cref="HsstPartitionedBTreeBuilder{TWriter,TReader,TPin}"/> and
/// <see cref="HsstPartitionedBTreeReader"/>.
/// </summary>
/// <remarks>
/// A hashtable is an array of cache-line buckets. Each bucket is <see cref="BucketBytes"/>
/// (64) bytes holding <see cref="WaysPerBucket"/> (12) ways struct-of-arrays so the tags can
/// be scanned with one SIMD compare:
/// <c>[Tag_0..Tag_11: 12×u16 LE][Offset_0..Offset_11: 12×u24 LE]</c> (tags in <c>[0,24)</c>,
/// offsets in <c>[24,60)</c>, 4 pad bytes). <c>Tag == 0</c> marks an empty way; live
/// placements force the tag to be ≥ 1, so the equality scan never matches an empty way.
/// <c>Offset_i</c> is the entry's flag-byte position stored as the <b>forward</b> distance
/// from the partition's data-section start (<c>Offset = EntryOffset − DataRegionStart</c>),
/// recovered as <c>entry_abs = bound.Offset + DataRegionStart + Offset</c>. The 3-byte offset
/// bounds a partition's data section to <see cref="MaxOffset"/> + 1 (16 MiB) — the inner index
/// sits after the data section so it does not consume offset budget. The table is best-effort:
/// a key whose bucket already holds 12 live tags is dropped, and the reader falls back to the
/// partition's inner B-tree.
/// </remarks>
internal static class HsstPartitionHashtable
{
    internal const int BucketBytes = 64;
    internal const int WaysPerBucket = 12;
    /// <summary>Byte width of a tag slot (u16).</summary>
    internal const int TagBytes = 2;
    /// <summary>Byte width of an offset slot (u24).</summary>
    internal const int OffsetBytes = 3;
    /// <summary>Byte offset of the offsets section within a bucket (the tags fill <c>[0, 24)</c>).</summary>
    internal const int OffsetsSectionStart = WaysPerBucket * TagBytes;
    /// <summary>Largest forward offset that fits the u24 offset slot (⇒ data section &lt; 16 MiB).</summary>
    internal const int MaxOffset = (1 << 24) - 1;

    /// <summary>
    /// Fixed size of a directory / single-partition metadata record (before the inner-root
    /// prefix bytes):
    /// <c>[InnerRootOffset: 6][InnerScopeEnd: 6][HashtableOffset: 6][DataRegionStart: 6][HashtableBucketCount: u24][InnerRootPrefixLen: u8]</c>.
    /// </summary>
    internal const int DirRecordFixedSize = 28;

    /// <summary>Hard cap on the bucket count (≈ 16 M buckets, ≈ 1 GiB region) — a runaway guard
    /// that fits the u24 record field; real partitions are bounded by the key-bytes / 16 MiB
    /// span thresholds well below this.</summary>
    internal const int MaxBucketCount = (1 << 24) - 1;

    /// <summary>Target hashtable load factor (keys / total ways), as a percent. Higher packs
    /// keys more densely (less memory) at the cost of more bucket overflow → more B-tree
    /// fallback on lookups. Drives <see cref="BucketCountFor"/>'s sizing only; the
    /// lookup-time bucket selection (<see cref="BucketIndex"/>) is unaffected.</summary>
    internal const int TargetUtilizationPercent = 75;

    /// <summary>Target keys per <see cref="WaysPerBucket"/>-way bucket implied by
    /// <see cref="TargetUtilizationPercent"/> (= 12 × 75% = 9).</summary>
    internal const int TargetKeysPerBucket = WaysPerBucket * TargetUtilizationPercent / 100;

    /// <summary>Byte size of a hashtable with <paramref name="bucketCount"/> buckets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long RegionSize(int bucketCount) => (long)bucketCount * BucketBytes;

    /// <summary>
    /// Pick the (non-power-of-two) bucket count for a partition of <paramref name="keyCount"/>
    /// keys: exactly <c>ceil(keyCount / <see cref="TargetKeysPerBucket"/>)</c> so a 12-way
    /// bucket meets the <see cref="TargetUtilizationPercent"/> load with no power-of-two
    /// rounding. At least 1, capped at <see cref="MaxBucketCount"/>; lookup-time selection uses
    /// Lemire reduction (<see cref="BucketIndex"/>), so any count works.
    /// </summary>
    internal static int BucketCountFor(int keyCount)
        => Math.Clamp((keyCount + TargetKeysPerBucket - 1) / TargetKeysPerBucket, 1, MaxBucketCount);

    /// <summary>64-bit mixing hash of <paramref name="key"/>; the bucket index is a Lemire
    /// reduction of its low 32 bits and the way tag its high 16 bits. Identical on write/read.</summary>
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

    /// <summary>
    /// Bucket index for <paramref name="hash"/> in a table of <paramref name="bucketCount"/>
    /// buckets via Lemire's multiply-shift reduction — `(low32 · bucketCount) >> 32` maps the
    /// uniform low 32 bits into <c>[0, bucketCount)</c> with one multiply, no div/mod, and works
    /// for any (non-power-of-two) count. The low 32 bits are disjoint from the tag's high 16.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int BucketIndex(ulong hash, int bucketCount) => (int)(((ulong)(uint)hash * (ulong)bucketCount) >> 32);

    /// <summary>Way tag for <paramref name="hash"/> — the high 16 bits, forced to ≥ 1 so 0 stays the empty marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Tag(ulong hash)
    {
        ushort tag = (ushort)(hash >> 48);
        return tag == 0 ? (ushort)1 : tag;
    }

    /// <summary>
    /// Insert <paramref name="offset"/> (= EntryOffset − DataRegionStart, the forward distance
    /// from the partition's data-section start) into <paramref name="buckets"/> for
    /// <paramref name="hash"/>. Returns false on bucket overflow (12 live tags) or on a u24
    /// offset overflow (defensive — never under the 16 MiB split) — the caller drops the key
    /// (best-effort, reader falls back to the inner B-tree).
    /// </summary>
    internal static bool TryInsert(Span<byte> buckets, int bucketCount, ulong hash, long offset)
    {
        if (offset < 0 || offset > MaxOffset) return false;
        int bucket = BucketIndex(hash, bucketCount);
        ushort tag = Tag(hash);
        Span<byte> b = buckets.Slice(bucket * BucketBytes, BucketBytes);
        for (int way = 0; way < WaysPerBucket; way++)
        {
            Span<byte> tagSlot = b.Slice(way * TagBytes, TagBytes);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tagSlot) != 0) continue; // occupied
            BinaryPrimitives.WriteUInt16LittleEndian(tagSlot, tag);
            WriteU24(b.Slice(OffsetsSectionStart + way * OffsetBytes, OffsetBytes), (uint)offset);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Bit mask of the ways in <paramref name="bucket"/> whose tag equals <paramref name="tag"/>
    /// (bit <c>i</c> set ⇒ way <c>i</c> matches). Scans all 12 tags with a single 256-bit
    /// equality compare where supported (masking off the 4 lanes that overlap the offsets
    /// section); empty ways (tag 0) never match a live tag (≥ 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint MatchMask(scoped ReadOnlySpan<byte> bucket, ushort tag) =>
        Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian
            ? Vector256.Equals(Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(bucket)).AsUInt16(), Vector256.Create(tag))
                .ExtractMostSignificantBits() & ((1u << WaysPerBucket) - 1)
            : MatchMaskScalar(bucket, tag);

    /// <summary>Endian-correct scalar equivalent of <see cref="MatchMask"/> (fallback + test cross-check).</summary>
    internal static uint MatchMaskScalar(scoped ReadOnlySpan<byte> bucket, ushort tag)
    {
        uint mask = 0;
        for (int way = 0; way < WaysPerBucket; way++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(bucket.Slice(way * TagBytes, TagBytes)) == tag)
                mask |= 1u << way;
        return mask;
    }

    /// <summary>Forward offset stored for way <paramref name="way"/> of a 64-byte <paramref name="bucket"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint OffsetAt(scoped ReadOnlySpan<byte> bucket, int way) =>
        ReadU24(bucket.Slice(OffsetsSectionStart + way * OffsetBytes, OffsetBytes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU24(scoped ReadOnlySpan<byte> src) =>
        (uint)(src[0] | (src[1] << 8) | (src[2] << 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU24(Span<byte> dest, uint value)
    {
        dest[0] = (byte)value;
        dest[1] = (byte)(value >> 8);
        dest[2] = (byte)(value >> 16);
    }
}
