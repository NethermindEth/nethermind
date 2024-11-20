// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

public class BlockStore([KeyFilter(DbNames.Blocks)] IDb blockDb) : IBlockStore
{
    private readonly BlockDecoder _blockDecoder = new();
    public const int CacheSize = 128 + 32;

    private readonly ClockCache<ValueHash256, Block>
        _blockCache = new(CacheSize);

    public void SetMetadata(byte[] key, byte[] value)
    {
        blockDb.Set(key, value);
    }

    public byte[]? GetMetadata(byte[] key)
    {
        return blockDb.Get(key);
    }

    public bool HasBlock(long blockNumber, Hash256 blockHash)
    {
        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        return blockDb.KeyExists(dbKey);
    }

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        // if we carry Rlp from the network message all the way here we could avoid encoding back to RLP here
        // Although cpu is the main bottleneck since NettyRlpStream uses pooled memory which avoid unnecessary allocations..
        using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);

        blockDb.Set(block.Number, block.Hash, newRlp.AsSpan(), writeFlags);
    }

    private static void GetBlockNumPrefixedKey(long blockNumber, Hash256 blockHash, Span<byte> output)
    {
        blockNumber.WriteBigEndian(output);
        blockHash!.Bytes.CopyTo(output[8..]);
    }

    public void Delete(long blockNumber, Hash256 blockHash)
    {
        _blockCache.Delete(blockHash);
        blockDb.Delete(blockNumber, blockHash);
        blockDb.Remove(blockHash.Bytes);
    }

    public Block? Get(long blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = false)
    {
        Block? b = blockDb.Get(blockNumber, blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
        if (b is not null) return b;
        return blockDb.Get(blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
    }

    public byte[]? GetRlp(long blockNumber, Hash256 blockHash)
    {
        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        var b = blockDb.Get(dbKey);
        if (b is not null) return b;
        return blockDb.Get(blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Hash256 blockHash)
    {
        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        MemoryManager<byte>? memoryOwner = blockDb.GetOwnedMemory(keyWithBlockNumber);
        memoryOwner ??= blockDb.GetOwnedMemory(blockHash.Bytes);

        return BlockDecoder.DecodeToReceiptRecoveryBlock(memoryOwner, memoryOwner?.Memory ?? Memory<byte>.Empty, RlpBehaviors.None);
    }

    public void Cache(Block block)
    {
        _blockCache.Set(block.Hash, block);
    }
}
