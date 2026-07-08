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

public class BlockStore : IBlockStore, IClearableCache
{
    public const int CacheSize = 128 + 32;

    private readonly IDb _blockDb;
    private readonly BlockDecoder _blockDecoder;
    // A block body is a re-execution input, not an output, so a lost body cannot be regenerated on
    // restart; deferral is opt-in even when the shared writer is running for receipts.
    private readonly IDeferredBlockDataWriter? _deferredWriter;
    private readonly IStatePersistenceBarrier _persistenceBarrier;

    // Blocks whose durable write is still queued: the immutable RLP (not the live Block, which the caller
    // mutates after suggesting). Removed only after the DB write, value-conditionally. Never evicts.
    private readonly ConcurrentDictionary<ValueHash256, PendingBlockEntry> _pendingBlocks = new();

    // Serialises a queued background write against a synchronous Delete of the same block.
    private readonly Lock _writeLock = new();

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
        _deferredWriter = deferBodies && deferredWriter is { Enabled: true } ? deferredWriter : null;
        _persistenceBarrier = persistenceBarrier ?? NullStatePersistenceBarrier.Instance;

        // The eager writer keeps the overlay shallow; this gate only guarantees that any straggler it
        // has not yet reached is durable before the block's state is. No-op when deferral is off.
        if (_deferredWriter is not null && _persistenceBarrier.IsEnabled)
        {
            _persistenceBarrier.Register(FlushBodiesUpTo);
        }
    }

    /// <summary>
    /// A block body whose durable write is still queued. A plain class (not a record) so value-conditional
    /// removal keys on reference identity; the block number lets the barrier flush entries up to a block.
    /// </summary>
    private sealed class PendingBlockEntry(ulong blockNumber, Hash256 blockHash, byte[] rlp)
    {
        public ulong BlockNumber { get; } = blockNumber;
        public Hash256 BlockHash { get; } = blockHash;
        public byte[] Rlp { get; } = rlp;
    }

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

        PendingBlockEntry entry = new(block.Number, block.Hash, rlp);
        _pendingBlocks[block.Hash.ValueHash256] = entry;
        _deferredWriter.Enqueue(() => PersistDeferred(entry));
    }

    private bool PersistDeferred(PendingBlockEntry entry)
    {
        lock (_writeLock)
        {
            // Skip if a Delete (or the gate racing the writer) already dropped this exact entry; the lock
            // makes check-then-write atomic against Delete so a write cannot resurrect a deleted block.
            ValueHash256 key = entry.BlockHash.ValueHash256;
            if (!_pendingBlocks.TryGetValue(key, out PendingBlockEntry? current) || !ReferenceEquals(current, entry))
            {
                return false;
            }

            _blockDb.Set(entry.BlockNumber, entry.BlockHash, entry.Rlp, WriteFlags.None);
            _pendingBlocks.TryRemove(new KeyValuePair<ValueHash256, PendingBlockEntry>(key, entry));
            return true;
        }
    }

    /// <summary>
    /// State persistence barrier hook: force any queued body write for a block up to
    /// <paramref name="blockNumber"/> to disk and fsync the blocks WAL, so it is durable before the
    /// block's state is persisted. Normally a no-op - the eager writer has already drained these.
    /// </summary>
    private void FlushBodiesUpTo(long blockNumber)
    {
        ulong upTo = (ulong)blockNumber;
        foreach (KeyValuePair<ValueHash256, PendingBlockEntry> kv in _pendingBlocks)
        {
            if (kv.Value.BlockNumber <= upTo)
            {
                PersistDeferred(kv.Value);
            }
        }

        // Unconditional: the eager writer's Set is WAL-buffered (WriteFlags.None) and nothing else fsyncs
        // it, so state must not become durable ahead of this - even when the writer, not the gate, wrote.
        _blockDb.Flush(onlyWal: true);
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
        if (_pendingBlocks.TryGetValue(blockHash.ValueHash256, out PendingBlockEntry? pending))
        {
            return _blockDecoder.Decode(pending.Rlp, rlpBehaviors);
        }

        Block? b = _blockDb.Get(blockNumber, blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
        if (b is not null) return b;
        return _blockDb.Get(blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
    }

    public byte[]? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        if (_pendingBlocks.TryGetValue(blockHash.ValueHash256, out PendingBlockEntry? pending))
        {
            return pending.Rlp;
        }

        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        byte[] b = _blockDb.Get(dbKey);
        if (b is not null) return b;
        return _blockDb.Get(blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(ulong blockNumber, Hash256 blockHash)
    {
        if (_pendingBlocks.TryGetValue(blockHash.ValueHash256, out PendingBlockEntry? pending))
        {
            Block? pendingBlock = _blockDecoder.Decode(pending.Rlp);
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
