// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Read-side helper for the <see cref="IndexType.PartitionedBTreeKeyFirst"/> (0x08) and
/// <see cref="IndexType.SinglePartitionHashtableBTreeKeyFirst"/> (0x09) layouts. Stateless
/// static methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without
/// copying its ref-struct state.
/// </summary>
/// <remarks>
/// Both variants resolve a partition's hashtable metadata, then run the same
/// <see cref="ProbeAndFallback{TReader,TPin}"/>: probe one 64-byte bucket and decode the entry
/// directly, falling back to the partition's inner key-first B-tree (via
/// <see cref="HsstBTreeReader.TrySeekFromRoot"/>) on any miss. They differ only in where the
/// metadata comes from: 0x08 floor-seeks a directory B-tree (reusing <see cref="HsstBTreeReader"/>)
/// to pick a partition; 0x09 has exactly one partition, so the metadata sits straight in the
/// trailer (no directory). All in-blob offsets are byte-0-relative, so every descent uses the
/// whole-blob <paramref name="bound"/>. See FORMAT.md.
/// </remarks>
internal static class HsstPartitionedBTreeReader
{
    /// <summary>0x08 lookup: floor-seek the directory for the partition, then probe + fall back.</summary>
    [SkipLocalsInit]
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        // Trailer: [...][DirRootSize u16][KeyLength u8][IndexType u8]; KeyLength sits at
        // bound end − 2 (IndexType already consumed by the HsstReader dispatcher).
        if (bound.Length < 2) return false;
        Span<byte> klBuf = stackalloc byte[1];
        if (!reader.TryRead(bound.Offset + bound.Length - 2, klBuf)) return false;
        int keyLength = klBuf[0];

        // Directory floor-seek: largest partition-first-key ≤ key.
        if (!HsstBTreeReader.TrySeek<TReader, TPin>(in reader, bound, key, exactMatch: false, keyFirst: true, out Bound metaBound))
            return false;

        // Decode the partition metadata record (the directory entry's value).
        if (metaBound.Length < HsstPartitionHashtable.DirRecordFixedSize) return false;
        Span<byte> rec = stackalloc byte[HsstPartitionHashtable.DirRecordFixedSize];
        if (!reader.TryRead(metaBound.Offset, rec)) return false;
        long innerRootOffset = ReadU48(rec);
        long innerScopeEnd = ReadU48(rec[6..]);
        long hashtableOffset = ReadU48(rec[12..]);
        int bucketCountLog2 = rec[18];
        int rootPrefixLen = rec[19];

        scoped ReadOnlySpan<byte> rootPrefix = default;
        if (rootPrefixLen > 0)
        {
            if (metaBound.Length < HsstPartitionHashtable.DirRecordFixedSize + rootPrefixLen) return false;
            Span<byte> rp = stackalloc byte[rootPrefixLen];
            if (!reader.TryRead(metaBound.Offset + HsstPartitionHashtable.DirRecordFixedSize, rp)) return false;
            rootPrefix = rp;
        }

        return ProbeAndFallback<TReader, TPin>(in reader, bound, key, exactMatch, keyLength,
            innerRootOffset, innerScopeEnd, hashtableOffset, bucketCountLog2, rootPrefix, out resultBound);
    }

    /// <summary>0x09 lookup: read the single partition's metadata from the trailer, then probe + fall back.</summary>
    [SkipLocalsInit]
    public static bool TrySeekSingle<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        Span<byte> prefixBuf = stackalloc byte[256];
        if (!ReadSinglePartitionTrailer<TReader, TPin>(in reader, bound, out int keyLength,
                out long innerRootOffset, out long innerScopeEnd, out long hashtableOffset,
                out int bucketCountLog2, prefixBuf, out int rootPrefixLen))
            return false;

        return ProbeAndFallback<TReader, TPin>(in reader, bound, key, exactMatch, keyLength,
            innerRootOffset, innerScopeEnd, hashtableOffset, bucketCountLog2, prefixBuf[..rootPrefixLen], out resultBound);
    }

    /// <summary>
    /// Parse the <see cref="IndexType.SinglePartitionHashtableBTreeKeyFirst"/> (0x09) trailer.
    /// Tail layout (low→high): <c>[InnerRootPrefix: prefixLen][Metadata: 20][KeyLength: u8][IndexType: u8]</c>.
    /// Reads the fixed record first (it carries prefixLen), then the prefix bytes that precede it
    /// into <paramref name="rootPrefixDest"/>. Shared by the reader and enumerator.
    /// </summary>
    [SkipLocalsInit]
    internal static bool ReadSinglePartitionTrailer<TReader, TPin>(
        scoped in TReader reader, Bound bound,
        out int keyLength, out long innerRootOffset, out long innerScopeEnd,
        out long hashtableOffset, out int bucketCountLog2,
        scoped Span<byte> rootPrefixDest, out int rootPrefixLen)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        keyLength = 0;
        innerRootOffset = innerScopeEnd = hashtableOffset = 0;
        bucketCountLog2 = rootPrefixLen = 0;

        int recSize = HsstPartitionHashtable.DirRecordFixedSize;
        if (bound.Length < 2 + recSize) return false;

        Span<byte> klBuf = stackalloc byte[1];
        if (!reader.TryRead(bound.Offset + bound.Length - 2, klBuf)) return false;
        keyLength = klBuf[0];

        long recPos = bound.Offset + bound.Length - 2 - recSize;
        Span<byte> rec = stackalloc byte[HsstPartitionHashtable.DirRecordFixedSize];
        if (!reader.TryRead(recPos, rec)) return false;
        innerRootOffset = ReadU48(rec);
        innerScopeEnd = ReadU48(rec[6..]);
        hashtableOffset = ReadU48(rec[12..]);
        bucketCountLog2 = rec[18];
        rootPrefixLen = rec[19];

        if (rootPrefixLen > 0)
        {
            if (rootPrefixLen > rootPrefixDest.Length) return false;
            if (recPos - rootPrefixLen < bound.Offset) return false;
            if (!reader.TryRead(recPos - rootPrefixLen, rootPrefixDest[..rootPrefixLen])) return false;
        }
        return true;
    }

    /// <summary>
    /// Shared tail of both variants: probe the partition's hashtable for an exact match, then
    /// fall back to walking its inner key-first B-tree from the recorded root.
    /// </summary>
    [SkipLocalsInit]
    private static bool ProbeAndFallback<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key, bool exactMatch,
        int keyLength, long innerRootOffset, long innerScopeEnd, long hashtableOffset,
        int bucketCountLog2, scoped ReadOnlySpan<byte> rootPrefix, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;

        // Hashtable probe — exact lookups only (floor/iteration use the sorted tree).
        if (exactMatch && bucketCountLog2 > 0)
        {
            ulong hash = HsstPartitionHashtable.Hash(key);
            int bucket = HsstPartitionHashtable.BucketIndex(hash, bucketCountLog2);
            uint tag = HsstPartitionHashtable.Tag(hash);
            long bucketAbs = bound.Offset + hashtableOffset + (long)bucket * HsstPartitionHashtable.BucketBytes;
            Span<byte> bucketBuf = stackalloc byte[HsstPartitionHashtable.BucketBytes];
            if (reader.TryRead(bucketAbs, bucketBuf))
            {
                for (int way = 0; way < HsstPartitionHashtable.WaysPerBucket; way++)
                {
                    uint wayTag = HsstPartitionHashtable.WayTag(bucketBuf, way);
                    if (wayTag == 0) break; // empty way → no further live entries in this bucket
                    if (wayTag != tag) continue;
                    long entryAbs = bound.Offset + hashtableOffset - HsstPartitionHashtable.WayOffset(bucketBuf, way);
                    // DecodeEntry verifies the full key under exactMatch, so a tag collision
                    // with a different key returns false and we keep scanning.
                    if (HsstBTreeReader.DecodeEntry<TReader, TPin>(in reader, bound, entryAbs, key,
                            exactMatch: true, keyFirst: true, keyLength, out resultBound))
                        return true;
                }
            }
        }

        // Fallback: walk the partition's inner B-tree from the recorded root.
        long rootStartAbs = bound.Offset + innerRootOffset;
        long scopeEndAbs = bound.Offset + innerScopeEnd;
        return HsstBTreeReader.TrySeekFromRoot<TReader, TPin>(in reader, bound, rootStartAbs, scopeEndAbs,
            rootPrefix, keyLength, key, exactMatch, keyFirst: true, out resultBound);
    }

    private static long ReadU48(scoped ReadOnlySpan<byte> src) =>
        src[0]
        | ((long)src[1] << 8)
        | ((long)src[2] << 16)
        | ((long)src[3] << 24)
        | ((long)src[4] << 32)
        | ((long)src[5] << 40);
}
