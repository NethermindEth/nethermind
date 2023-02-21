using Nethermind.Int256;
using Nethermind.Verkle.Utils;

namespace Nethermind.Verkle.Tree;

public readonly struct AccountHeader
{
    public const int Version = 0;
    public const int Balance = 1;
    public const int Nonce = 2;
    public const int CodeHash = 3;
    public const int CodeSize = 4;

    private const int MainStorageOffsetExponent = 31;
    private static readonly UInt256 MainStorageOffsetBase = 256;
    private static readonly UInt256 MainStorageOffset = MainStorageOffsetBase << MainStorageOffsetExponent;

    private static readonly UInt256 HeaderStorageOffset = 64;
    private static readonly UInt256 CodeOffset = 128;
    private static readonly UInt256 VerkleNodeWidth = 256;

    public static byte[] GetTreeKeyPrefix(ReadOnlySpan<byte> address20, UInt256 treeIndex)
    {
        return PedersenHash.Hash(ToAddress32(address20), treeIndex);
    }

    public static Span<byte> ToAddress32(ReadOnlySpan<byte> address20)
    {
        Span<byte> destination = (Span<byte>) new byte[32];
        Span<byte> sl = destination[12..];
        address20.CopyTo(sl);
        return destination;
    }

    public static byte[] GetTreeKeyPrefixAccount(byte[] address) => GetTreeKeyPrefix(address, 0);

    public static byte[] GetTreeKey(byte[] address, UInt256 treeIndex, byte subIndexBytes)
    {
        byte[] treeKeyPrefix = GetTreeKeyPrefix(address, treeIndex);
        treeKeyPrefix[31] = subIndexBytes;
        return treeKeyPrefix;
    }

    public static byte[] GetTreeKeyForVersion(byte[] address) => GetTreeKey(address, UInt256.Zero, Version);
    public static byte[] GetTreeKeyForBalance(byte[] address) => GetTreeKey(address, UInt256.Zero, Balance);
    public static byte[] GetTreeKeyForNonce(byte[] address) => GetTreeKey(address, UInt256.Zero, Nonce);
    public static byte[] GetTreeKeyForCodeCommitment(byte[] address) => GetTreeKey(address, UInt256.Zero, CodeHash);
    public static byte[] GetTreeKeyForCodeSize(byte[] address) => GetTreeKey(address, UInt256.Zero, CodeSize);


    public static byte[] GetTreeKeyForCodeChunk(byte[] address, UInt256 chunk)
    {
        UInt256 chunkOffset = CodeOffset + chunk;
        UInt256 treeIndex = chunkOffset / VerkleNodeWidth;
        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
    }

    public static byte[] GetTreeKeyForStorageSlot(byte[] address, UInt256 storageKey)
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
