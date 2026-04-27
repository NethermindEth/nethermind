// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.BlockRangeTrieForest;

/// <summary>
/// Key encoding for <see cref="IBlockRangeTrieForest"/>.
///
/// Mirrors <c>NodeStorage.GetHalfPathNodeStoragePathSpan</c> but prefixes every key with a
/// 4-byte big-endian block-range index so all keys for a given range sort together,
/// enabling efficient range-bounded scans and bulk deletions.
///
/// State  (46 bytes): range:4 | section:1 | pathPrefix:8 | pathLen:1 | hash:32
/// Storage (78 bytes): range:4 | section=2:1 | addr:32 | pathPrefix:8 | pathLen:1 | hash:32
///
/// section: 0 if state and path.Length &lt;= 5; 1 if state and path.Length &gt; 5; 2 if storage.
/// </summary>
public static class BlockRangeForestKey
{
    public const int StateKeyLength = 46;
    public const int StorageKeyLength = 78;
    public const int RangePrefixLength = 4;
    private const int TopStateBoundary = 5;

    public static void EncodeState(Span<byte> output, long blockRange, in TreePath path, in ValueHash256 hash)
    {
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)blockRange);
        output[4] = path.Length <= TopStateBoundary ? (byte)0 : (byte)1;
        CopyNormalizedPathPrefix(output[5..13], path);
        output[13] = (byte)path.Length;
        hash.Bytes.CopyTo(output[14..]);
    }

    public static void EncodeStorage(Span<byte> output, long blockRange, in ValueHash256 address, in TreePath path, in ValueHash256 hash)
    {
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)blockRange);
        output[4] = 2;
        address.Bytes.CopyTo(output[5..]);
        CopyNormalizedPathPrefix(output[37..45], path);
        output[45] = (byte)path.Length;
        hash.Bytes.CopyTo(output[46..]);
    }

    /// <summary>
    /// Copies only the meaningful nibbles of <paramref name="path"/> into <paramref name="dest"/> (8 bytes),
    /// zeroing out any bits beyond the path length. This ensures keys are canonical regardless of
    /// garbage bits stored in the underlying hash-derived path bytes.
    /// </summary>
    private static void CopyNormalizedPathPrefix(Span<byte> dest, in TreePath path)
    {
        // Each nibble occupies 4 bits; path.Length nibbles → ceil(path.Length / 2) meaningful bytes.
        int length = path.Length;
        int fullBytes = length >> 1;      // bytes that are fully covered by the path
        int hasOddNibble = length & 1;   // 1 if there is a trailing nibble in a half-full byte

        ReadOnlySpan<byte> src = path.Path.BytesAsSpan;
        src[..Math.Min(fullBytes, 8)].CopyTo(dest);

        int written = Math.Min(fullBytes, 8);
        if (hasOddNibble != 0 && written < 8)
        {
            // Upper nibble of this byte is meaningful; zero the lower nibble.
            dest[written] = (byte)(src[written] & 0xf0);
            written++;
        }
        dest[written..].Clear();
    }

    /// <summary>Encode only the 4-byte range prefix into <paramref name="output"/>.</summary>
    public static void EncodeRangePrefix(Span<byte> output, long blockRange) =>
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)blockRange);

    public static long DecodeBlockRange(ReadOnlySpan<byte> key) =>
        BinaryPrimitives.ReadUInt32BigEndian(key);

    /// <summary>
    /// Returns the exclusive upper-bound key for a range scan: the 4-byte prefix for
    /// <paramref name="blockRange"/> + 1, padded with zeros to full length so
    /// <see cref="ISortedKeyValueStore.GetViewBetween"/> correctly excludes the range.
    /// </summary>
    public static byte[] RangeUpperBoundKey(long blockRange)
    {
        byte[] key = new byte[RangePrefixLength];
        BinaryPrimitives.WriteUInt32BigEndian(key, (uint)(blockRange + 1));
        return key;
    }

    public static long BlockRangeForBlock(long blockNumber, int blockRangePerForest) =>
        blockNumber / blockRangePerForest;
}
