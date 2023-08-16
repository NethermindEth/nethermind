using System;
using Nethermind.Core.Caching;
using Nethermind.Int256;

namespace Nethermind.Core.Verkle;

public readonly struct AccountHeader
{
    public const int Version = 0;
    public const int Balance = 1;
    public const int Nonce = 2;
    public const int CodeHash = 3;
    public const int CodeSize = 4;

    private const int MainStorageOffsetExponent = 31;
    private const int MainStorageOffsetBase = 256;
    private const int HeaderStorageOffset = 64;
    private const int CodeOffset = 128;
    private const int VerkleNodeWidth = 256;

    private static readonly UInt256 MainStorageOffset = (UInt256)MainStorageOffsetBase << MainStorageOffsetExponent;

    private static readonly LruCache<(byte[], UInt256), Pedersen> _keyCache = new(1000000, 10000, "Verkle Key Cache");

    private static Pedersen GetTreeKeyPrefix(ReadOnlySpan<byte> address20, UInt256 treeIndex)
    {
        if (_keyCache.TryGet((address20.ToArray(), treeIndex), out Pedersen value)) return value;
        value = Pedersen.Compute(address20, treeIndex);
        _keyCache.Set((address20.ToArray(), treeIndex), value);
        return value;
    }

    public static Pedersen GetTreeKeyPrefixAccount(byte[] address) => GetTreeKeyPrefix(address, 0);

    public static Pedersen GetTreeKey(byte[] address, UInt256 treeIndex, byte subIndexBytes)
    {
        Pedersen treeKeyPrefix = GetTreeKeyPrefix(address, treeIndex);
        treeKeyPrefix.SuffixByte = subIndexBytes;
        return treeKeyPrefix;
    }

    public static Pedersen GetTreeKeyForCodeChunk(byte[] address, UInt256 chunk)
    {
        UInt256 chunkOffset = CodeOffset + chunk;
        UInt256 treeIndex = chunkOffset / VerkleNodeWidth;
        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
    }

    public static Pedersen GetTreeKeyForStorageSlot(byte[] address, UInt256 storageKey)
    {
        UInt256 pos;

        if (storageKey < CodeOffset - HeaderStorageOffset) pos = HeaderStorageOffset + storageKey;
        else pos = MainStorageOffset + storageKey;

        UInt256 treeIndex = pos / VerkleNodeWidth;

        UInt256.Mod(pos, VerkleNodeWidth, out UInt256 subIndex);
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
    }

    public static void FillTreeAndSubIndexForChunk(UInt256 chunkId, ref Span<byte> subIndexBytes, out UInt256 treeIndex)
    {
        UInt256 chunkOffset = CodeOffset + chunkId;
        treeIndex = chunkOffset / VerkleNodeWidth;
        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        subIndex.ToBigEndian(subIndexBytes);
    }
}
