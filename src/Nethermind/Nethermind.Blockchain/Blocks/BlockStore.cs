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
    private readonly DeferredWriteOverlay<byte[]>? _pending;

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
            _pending = new DeferredWriteOverlay<byte[]>(deferredWriter, (number, hash, rlp) => _blockDb.Set(number, hash, rlp, WriteFlags.None));
            (persistenceBarrier ?? NullStatePersistenceBarrier.Instance).RegisterFlush(() => _blockDb.Flush(onlyWal: true));
        }
    }

    public void SetMetadata(byte[] key, byte[] value) => _blockDb.Set(key, value);

    public byte[]? GetMetadata(byte[] key) => _blockDb.Get(key);

    public bool HasBlock(ulong blockNumber, Hash256 blockHash)
    {
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

        // Encode now into an immutable buffer: the caller mutates the live Block after suggesting (BAL fields
        // nulled, total difficulty hydrated later); only the database write defers.
        byte[] rlp;
        using (ArrayPoolSpan<byte> encoded = _blockDecoder.EncodeToArrayPoolSpan(block))
        {
            rlp = ((ReadOnlySpan<byte>)encoded).ToArray();
        }

        _pending.Publish(block.Number, block.Hash, rlp);
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
        // Decode a fresh instance from the pending bytes, as the DB path would; callers must not see the live Block.
        if (_pending is not null && _pending.TryGet(blockHash, out byte[] pendingRlp))
        {
            return _blockDecoder.Decode(pendingRlp, rlpBehaviors);
        }

        Block? b = _blockDb.Get(blockNumber, blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
        if (b is not null) return b;
        return _blockDb.Get(blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
    }

    public byte[]? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        if (_pending is not null && _pending.TryGet(blockHash, out byte[] pendingRlp))
        {
            return pendingRlp;
        }

        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        byte[] b = _blockDb.Get(dbKey);
        if (b is not null) return b;
        return _blockDb.Get(blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(ulong blockNumber, Hash256 blockHash)
    {
        if (_pending is not null && _pending.TryGet(blockHash, out byte[] pendingRlp))
        {
            Block? pendingBlock = _blockDecoder.Decode(pendingRlp);
            return pendingBlock is null ? null : new ReceiptRecoveryBlock(pendingBlock);
        }

        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        MemoryManager<byte>? memoryOwner = _blockDb.GetOwnedMemory(keyWithBlockNumber);
        memoryOwner ??= _blockDb.GetOwnedMemory(blockHash.Bytes);
        if (memoryOwner is null) return null;

        return _blockDecoder.DecodeToReceiptRecoveryBlock(memoryOwner, memoryOwner.Memory, RlpBehaviors.None);
    }

    public void Cache(Block block) =>
        // Cache a sanitized copy to avoid retaining large BAL/account-change
        // structures, without mutating the original block instance which may
        // still be used by downstream consumers (e.g., TxPool reads and
        // disposes AccountChanges after this call).
        _blockCache.Set(in block.Hash.ValueHash256, new(block.Header, block.Body));

    void IClearableCache.ClearCache() => _blockCache.Clear();
}
