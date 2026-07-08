// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

public class BlockStore([KeyFilter(DbNames.Blocks)] IDb blockDb, IHeaderDecoder? headerDecoder = null, IDeferredBlockDataWriter? deferredWriter = null, bool deferBodies = false) : IBlockStore, IClearableCache
{
    public const int CacheSize = 128 + 32;

    private readonly IDb _blockDb = blockDb;
    private readonly BlockDecoder _blockDecoder = new(headerDecoder ?? new HeaderDecoder());
    // A block body is a re-execution input, not an output, so a lost body cannot be regenerated on
    // restart; deferral is opt-in even when the shared writer is running for receipts.
    private readonly IDeferredBlockDataWriter? _deferredWriter = deferBodies && deferredWriter is { Enabled: true } ? deferredWriter : null;

    // Source of truth for blocks whose durable write is still queued on the deferred writer. Holds
    // the immutable RLP encoded synchronously in InsertDeferred - not the live Block, which the
    // caller mutates (BAL nulling, TD hydration) after suggesting. Removed only after the database
    // write returns (value-conditionally, so a flush removes only the entry it wrote). Never evicts.
    private readonly ConcurrentDictionary<ValueHash256, byte[]> _pendingBlocks = new();

    // Serialises a queued background write against a synchronous Delete of the same block.
    private readonly Lock _writeLock = new();

    private readonly AssociativeCache<ValueHash256, Block>
        _blockCache = new(CacheSize);

    public void SetMetadata(byte[] key, byte[] value) => _blockDb.Set(key, value);

    public byte[]? GetMetadata(byte[] key) => _blockDb.Get(key);

    public bool HasBlock(ulong blockNumber, Hash256 blockHash)
    {
        if (_pendingBlocks.ContainsKey(blockHash.ValueHash256)) return true;

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
        if (_deferredWriter is null)
        {
            Insert(block);
            return;
        }

        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        // Encode now, into an immutable buffer: the caller mutates the live Block after suggesting
        // (the BAL store nulls its access-list fields; total difficulty is hydrated later), and only
        // the database write is worth deferring off the engine path.
        byte[] rlp;
        using (ArrayPoolSpan<byte> encoded = _blockDecoder.EncodeToArrayPoolSpan(block))
        {
            rlp = ((ReadOnlySpan<byte>)encoded).ToArray();
        }

        ulong blockNumber = block.Number;
        ValueHash256 blockHash = block.Hash.ValueHash256;
        _pendingBlocks[blockHash] = rlp;
        _deferredWriter.Enqueue(() => PersistDeferred(blockNumber, block.Hash, blockHash, rlp));
    }

    private void PersistDeferred(ulong blockNumber, Hash256 blockHash, ValueHash256 blockHashValue, byte[] rlp)
    {
        lock (_writeLock)
        {
            // Skip if a synchronous Delete already dropped this exact entry; the lock makes the
            // check-then-write atomic against Delete so a write cannot resurrect a deleted block.
            if (!_pendingBlocks.TryGetValue(blockHashValue, out byte[]? current) || !ReferenceEquals(current, rlp))
            {
                return;
            }

            _blockDb.Set(blockNumber, blockHash, rlp, WriteFlags.None);
            _pendingBlocks.TryRemove(new KeyValuePair<ValueHash256, byte[]>(blockHashValue, rlp));
        }
    }

    public void Delete(ulong blockNumber, Hash256 blockHash)
    {
        lock (_writeLock)
        {
            _pendingBlocks.TryRemove(blockHash.ValueHash256, out _);
            _blockCache.Delete(in blockHash.ValueHash256);
            _blockDb.Delete(blockNumber, blockHash);
            _blockDb.Remove(blockHash.Bytes);
        }
    }

    public Block? Get(ulong blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = false)
    {
        // Decode a fresh instance from the pending bytes, matching the database path's semantics
        // (callers must not observe the mutable live Block held elsewhere).
        if (_pendingBlocks.TryGetValue(blockHash.ValueHash256, out byte[]? pendingRlp))
        {
            return _blockDecoder.Decode(pendingRlp, rlpBehaviors);
        }

        Block? b = _blockDb.Get(blockNumber, blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
        if (b is not null) return b;
        return _blockDb.Get(blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
    }

    public byte[]? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        if (_pendingBlocks.TryGetValue(blockHash.ValueHash256, out byte[]? pendingRlp))
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
        if (_pendingBlocks.TryGetValue(blockHash.ValueHash256, out byte[]? pendingRlp))
        {
            Block? pending = _blockDecoder.Decode(pendingRlp);
            return pending is null ? null : new ReceiptRecoveryBlock(pending);
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
