// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

public class BlockStore([KeyFilter(DbNames.Blocks)] IDb blockDb, IHeaderDecoder headerDecoder = null) : IBlockStore, IClearableCache
{
    private readonly BlockDecoder _blockDecoder = new(headerDecoder ?? new HeaderDecoder());

    // Hybrid cache to balance two workloads:
    //   - Head-adjacent blocks (block processor parent lookups, reorg, head RPCs)
    //     stay in a reserved pool that historical reads cannot evict.
    //   - Historical RPC reads (indexers, explorers) populate a separate LRU
    //     pool so repeated reads of the same old block hit cache.
    // Total capacity matches geth's blockCacheLimit (core/blockchain.go:125).
    public const int HeadCacheSize = 128;
    public const int HistoricalCacheSize = 128;
    public const int CacheSize = HeadCacheSize + HistoricalCacheSize;

    private readonly AssociativeCache<ValueHash256, Block> _headBlockCache = new(HeadCacheSize);
    private readonly AssociativeCache<ValueHash256, Block> _historicalBlockCache = new(HistoricalCacheSize);

    public void SetMetadata(byte[] key, byte[] value) => blockDb.Set(key, value);

    public byte[]? GetMetadata(byte[] key) => blockDb.Get(key);

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

    public void Delete(long blockNumber, Hash256 blockHash)
    {
        _headBlockCache.Delete(in blockHash.ValueHash256);
        _historicalBlockCache.Delete(in blockHash.ValueHash256);
        blockDb.Delete(blockNumber, blockHash);
        blockDb.Remove(blockHash.Bytes);
    }

    public Block? Get(long blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = false)
    {
        // Check the head-reserved pool first — most likely hit for hot blocks.
        if (_headBlockCache.TryGet(in blockHash.ValueHash256, out Block? cached) && cached is not null)
            return cached;

        // Fall through to historical cache (checked inside blockDb.Get) + DB.
        Block? b = blockDb.Get(blockNumber, blockHash, _blockDecoder, _historicalBlockCache, rlpBehaviors, shouldCache);
        if (b is not null) return b;
        return blockDb.Get(blockHash, _blockDecoder, _historicalBlockCache, rlpBehaviors, shouldCache);
    }

    public byte[]? GetRlp(long blockNumber, Hash256 blockHash)
    {
        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        byte[] b = blockDb.Get(dbKey);
        if (b is not null) return b;
        return blockDb.Get(blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Hash256 blockHash)
    {
        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        MemoryManager<byte>? memoryOwner = blockDb.GetOwnedMemory(keyWithBlockNumber);
        memoryOwner ??= blockDb.GetOwnedMemory(blockHash.Bytes);
        if (memoryOwner is null) return null;

        return _blockDecoder.DecodeToReceiptRecoveryBlock(memoryOwner, memoryOwner.Memory, RlpBehaviors.None);
    }

    public void Cache(Block block, bool isNearHead = false)
    {
        // Cache a sanitized copy to avoid retaining large BAL/account-change
        // structures, without mutating the original block instance which may
        // still be used by downstream consumers (e.g., TxPool reads and
        // disposes AccountChanges after this call).
        Block sanitized = new(block.Header, block.Body);
        if (isNearHead)
            _headBlockCache.Set(in block.Hash.ValueHash256, sanitized);
        else
            _historicalBlockCache.Set(in block.Hash.ValueHash256, sanitized);
    }

    void IClearableCache.ClearCache()
    {
        _headBlockCache.Clear();
        _historicalBlockCache.Clear();
    }
}
