using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Verkle;

public readonly struct AccountHeader
{
    public const int BasicDataLeafKey = 0;
    public const int CodeHash = 1;

    public const int VersionOffset = 0;
    public const int NonceOffset = 4;
    public const int CodeSizeOffset = 12;
    public const int BalanceOffset = 16;

    public const int VersionBytesLength = 1;
    public const int NonceBytesLength = 8;
    public const int CodeSizeBytesLength = 4;
    public const int BalanceBytesLength = 16;

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

    public static Account BasicDataToAccount(in ReadOnlySpan<byte> basicData, Hash256 codeHash)
    {
        // since values are encoded little endian - we need the full 32 bytes to decode properly
        Span<byte> dataSpan = stackalloc byte[32];

        byte version = basicData[0];

        basicData.Slice(NonceOffset, NonceBytesLength).CopyTo(dataSpan[..NonceBytesLength]);
        var nonce = new UInt256(dataSpan);

        dataSpan.Clear();
        basicData.Slice(CodeSizeOffset, CodeSizeBytesLength).CopyTo(dataSpan[..CodeSizeBytesLength]);
        var codeSize = new UInt256(dataSpan);

        dataSpan.Clear();
        basicData.Slice(BalanceOffset, BalanceBytesLength).CopyTo(dataSpan[..BalanceBytesLength]);
        var balance = new UInt256(dataSpan);

        return new Account(nonce, balance, codeSize, version, Keccak.EmptyTreeHash, codeHash);
    }

    public static AccountStruct BasicDataToAccountStruct(in ReadOnlySpan<byte> basicData, ValueHash256 codeHash)
    {
        // since values are encoded little endian - we need the full 32 bytes to decode properly
        Span<byte> dataSpan = stackalloc byte[32];

        byte version = basicData[0];

        basicData.Slice(NonceOffset, NonceBytesLength).CopyTo(dataSpan[..NonceBytesLength]);
        var nonce = new UInt256(dataSpan);

        dataSpan.Clear();
        basicData.Slice(CodeSizeOffset, CodeSizeBytesLength).CopyTo(dataSpan[..CodeSizeBytesLength]);
        var codeSize = new UInt256(dataSpan);

        dataSpan.Clear();
        basicData.Slice(BalanceOffset, BalanceBytesLength).CopyTo(dataSpan[..BalanceBytesLength]);
        var balance = new UInt256(dataSpan);

        return new AccountStruct(nonce, balance, codeSize, version, Keccak.EmptyTreeHash, codeHash);
    }

    public static byte[] AccountToBasicData(Account account)
    {
        byte[] basicData = new byte[32];
        Span<byte> basicDataSpan = basicData;

        // TODO: should we convert balance to Uint128 and then directly decode to span
        Span<byte> balanceBytes = stackalloc byte[32];
        account.Balance.ToLittleEndian(balanceBytes);
        balanceBytes[..BalanceBytesLength].CopyTo(basicDataSpan.Slice(BalanceOffset, BalanceBytesLength));

        // we know that version is just 1 byte
        byte version = account.Version;
        basicData[0] = version;

        // we know that nonce is just 8 bytes
        ulong nonce = account.Nonce.u0;
        BinaryPrimitives.WriteUInt64LittleEndian(basicDataSpan.Slice(NonceOffset, NonceBytesLength), nonce);

        // we know that codeSize is just 4 bytes
        uint codeSize = (uint)account.CodeSize.u0;
        BinaryPrimitives.WriteUInt32LittleEndian(basicDataSpan.Slice(CodeSizeOffset, CodeSizeBytesLength), codeSize);

        return basicData;
    }
}
