// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

public class BlockStore : IBlockStore, IClearableCache
{
    public const int CacheSize = 128 + 32;

    private readonly IDb _blockDb;
    private readonly BlockDecoder _blockDecoder;
    // A block body is a re-execution input, so a lost body cannot be regenerated; deferral is opt-in. Null when off.
    // Holds a sanitized header+body snapshot (not the live block, not encoded bytes): the body RLP depends only
    // on header/txs/uncles/withdrawals, none mutated after processing, so encoding defers to the consumer.
    private readonly DeferredWriteOverlay<Block>? _pending;

    private readonly AssociativeCache<ValueHash256, Block>
        _blockCache = new(CacheSize);

    public BlockStore(
        [KeyFilter(DbNames.Blocks)] IDb blockDb,
        IHeaderDecoder? headerDecoder = null,
        IDeferredBlockDataWriter? deferredWriter = null,
        bool deferBodies = false,
        IStatePersistenceBarrier? persistenceBarrier = null)
    {
        _blockDb = blockDb;
        _blockDecoder = new(headerDecoder ?? new HeaderDecoder());

        if (deferBodies && deferredWriter is { Enabled: true })
        {
            _pending = new DeferredWriteOverlay<Block>(deferredWriter, WriteBlock);
            (persistenceBarrier ?? NullStatePersistenceBarrier.Instance).RegisterFlush(() => _blockDb.Flush(onlyWal: true));
        }
    }

    public void SetMetadata(byte[] key, byte[] value) => _blockDb.Set(key, value);

    public byte[]? GetMetadata(byte[] key) => _blockDb.Get(key);

    public bool HasBlock(ulong blockNumber, Hash256 blockHash)
    {
        ValueHash256 cacheKey = blockHash.ValueHash256;
        if (_blockCache.TryGetNoRefresh(in cacheKey, out _)) return true;
        if (_pending?.Contains(blockHash) == true) return true;

        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        return _blockDb.KeyExists(dbKey);
    }

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        using ArrayPoolSpan<byte> rlp = _blockDecoder.EncodeToArrayPoolSpan(block);
        _blockDb.Set(block.Number, block.Hash, rlp, writeFlags);
    }

    public void InsertDeferred(Block block)
    {
        if (_pending is null)
        {
            Insert(block);
            return;
        }

        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        // Snapshot header+body only - a distinct instance from the live block, which additionally carries large
        // BAL/account-change structures downstream consumers dispose. No encode or copy here: the body RLP depends
        // solely on header/txs/uncles/withdrawals (none mutated after processing), so encoding defers to the consumer.
        // Carry the pre-encoded transactions (populated when built from an ExecutionPayload) so the consumer reuses
        // them instead of re-encoding every tx; the arrays are immutable encoded bytes, so sharing the reference is safe.
        _pending.Publish(block.Number, block.Hash, new Block(block.Header, block.Body) { EncodedTransactions = block.EncodedTransactions });
    }

    private void WriteBlock(ulong blockNumber, Hash256 blockHash, Block block)
    {
        // Runs on the deferred-writer consumer: encode here, off the processing path.
        using ArrayPoolSpan<byte> rlp = _blockDecoder.EncodeToArrayPoolSpan(block);
        _blockDb.Set(blockNumber, blockHash, rlp, WriteFlags.None);
        block.EncodedTransactions = null;
    }

    public void Delete(ulong blockNumber, Hash256 blockHash)
    {
        if (_pending is not null)
        {
            _pending.Remove(blockHash, () => DeleteFromDb(blockNumber, blockHash));
        }
        else
        {
            DeleteFromDb(blockNumber, blockHash);
        }
    }

    private void DeleteFromDb(ulong blockNumber, Hash256 blockHash)
    {
        _blockCache.Delete(in blockHash.ValueHash256);
        _blockDb.Delete(blockNumber, blockHash);
        _blockDb.Remove(blockHash.Bytes);
    }

    public Block? Get(ulong blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = false)
    {
        ValueHash256 cacheKey = blockHash.ValueHash256;
        if (_blockCache.TryGet(in cacheKey, out Block? cachedBlock)) return cachedBlock;

        // Served from the pending snapshot - a distinct instance from the live block - until the write lands.
        if (_pending is not null && _pending.TryGet(blockHash, out Block? pendingBlock))
        {
            return pendingBlock;
        }

        Block? block = _blockDb.Get(blockNumber, blockHash, _blockDecoder,
            cache: (AssociativeCache<ValueHash256, Block>?)null, rlpBehaviors: rlpBehaviors, shouldCache: false)
            ?? _blockDb.Get(blockHash, _blockDecoder,
                cache: (AssociativeCache<ValueHash256, Block>?)null, rlpBehaviors: rlpBehaviors, shouldCache: false);

        if (shouldCache && block is not null)
        {
            _blockCache.Set(in cacheKey, block);
        }

        return block;
    }

    public byte[]? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        // Bytes are needed here, so encode the snapshot on demand - paid only on a rare read of a still-pending block.
        if (_pending is not null && _pending.TryGet(blockHash, out Block? pendingBlock))
        {
            return _blockDecoder.Encode(pendingBlock).Bytes;
        }

        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        byte[] b = _blockDb.Get(dbKey);
        if (b is not null) return b;
        return _blockDb.Get(blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(ulong blockNumber, Hash256 blockHash)
    {
        ValueHash256 cacheKey = blockHash.ValueHash256;
        if (_blockCache.TryGet(in cacheKey, out Block? cachedBlock))
        {
            return new ReceiptRecoveryBlock(cachedBlock);
        }

        if (_pending is not null && _pending.TryGet(blockHash, out Block? pendingBlock))
        {
            return new ReceiptRecoveryBlock(pendingBlock);
        }

        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        MemoryManager<byte>? memoryOwner = _blockDb.GetOwnedMemory(keyWithBlockNumber);
        memoryOwner ??= _blockDb.GetOwnedMemory(blockHash.Bytes);
        if (memoryOwner is null) return null;

        return _blockDecoder.DecodeToReceiptRecoveryBlock(memoryOwner, memoryOwner.Memory, RlpBehaviors.None);
    }

    public void Cache(Block block)
    {
        ValueHash256 cacheKey = block.Hash.ValueHash256;
        Block cachedBlock = _pending is not null && _pending.TryGet(block.Hash!, out Block? pendingBlock)
            ? pendingBlock
            // Cache a sanitized copy to avoid retaining large BAL/account-change structures without mutating
            // the original block, which downstream consumers may still use and dispose.
            : new Block(block.Header, block.Body);

        _blockCache.Set(in cacheKey, cachedBlock);
    }

    void IClearableCache.ClearCache() => _blockCache.Clear();
}
