using System;
using System.Collections.Generic;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Verkle;

public readonly struct AccountHeader
{
    public const int Version = 0;
    public const int Balance = 1;
    public const int Nonce = 2;
    public const int CodeHash = 3;
    public const int CodeSize = 4;

    private const int MainStorageOffsetExponent = 8 * 31;
    private const int MainStorageOffsetBase = 1;
    private const int HeaderStorageOffset = 64;
    private const int CodeOffset = 128;
    private const int VerkleNodeWidth = 256;

    private static readonly UInt256 MainStorageOffset = ((UInt256)MainStorageOffsetBase << MainStorageOffsetExponent) >> 8;

    private static readonly LruCache<(byte[], UInt256), byte[]> _keyCache = new(
        1000000, 10000, "Verkle Key Cache", new ArrayAndUintComparer());

    private class ArrayAndUintComparer : IEqualityComparer<(byte[], UInt256)>
    {
        public bool Equals((byte[], UInt256) x, (byte[], UInt256) y)
        {
            return Bytes.AreEqual(x.Item1, y.Item1) && ((Object)x.Item2).Equals(y.Item2);
        }

        public int GetHashCode((byte[], UInt256) obj)
        {
            return HashCode.Combine(obj.Item1.GetSimplifiedHashCode(), obj.Item2);
        }
    }

    public static byte[] GetTreeKeyPrefix(ReadOnlySpan<byte> address20, UInt256 treeIndex)
    {
        if (_keyCache.TryGet((address20.ToArray(), treeIndex), out byte[] value)) return value;
        value = PedersenHash.ComputeHashBytes(address20, treeIndex);
        _keyCache.Set((address20.ToArray(), treeIndex), value);
        return value;
    }

    public static Hash256 GetTreeKey(ReadOnlySpan<byte> address, UInt256 treeIndex, byte subIndexBytes)
    {
        var treeKeyPrefix = GetTreeKeyPrefix(address, treeIndex);
        treeKeyPrefix[31] = subIndexBytes;
        return new Hash256(treeKeyPrefix);
    }

    public static Hash256 GetTreeKeyForCodeChunk(byte[] address, UInt256 chunk)
    {
        UInt256 chunkOffset = CodeOffset + chunk;
        UInt256 treeIndex = chunkOffset / VerkleNodeWidth;
        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
    }

    public static Hash256 GetTreeKeyForStorageSlot(ReadOnlySpan<byte> address, UInt256 storageKey)
    {
        if (storageKey < (CodeOffset - HeaderStorageOffset))
            return GetTreeKey(address, UInt256.Zero, (HeaderStorageOffset + storageKey).ToBigEndian()[31]);

        byte subIndex = storageKey.ToBigEndian()[31];
        UInt256 treeIndex = storageKey >> 8;
        treeIndex += MainStorageOffset;
        return GetTreeKey(address, treeIndex, subIndex);
    }

    public static void FillTreeAndSubIndexForChunk(UInt256 chunkId, ref Span<byte> subIndexBytes, out UInt256 treeIndex)
    {
        UInt256 chunkOffset = CodeOffset + chunkId;
        treeIndex = chunkOffset / VerkleNodeWidth;
        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        subIndex.ToBigEndian(subIndexBytes);
    }
}
