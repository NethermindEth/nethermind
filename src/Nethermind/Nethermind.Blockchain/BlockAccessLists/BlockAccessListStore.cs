// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Blockchain.BlockAccessLists;

public class BlockAccessListStore : IBlockAccessListStore
{
    private const int KeyLength = 40;

    private readonly IDb _balDb;
    private readonly BlockAccessListDecoder _balDecoder;
    // Like a block body, a lost BAL cannot be regenerated once its state is persisted, so deferral is
    // opt-in and coupled to body deferral.
    private readonly IDeferredBlockDataWriter? _deferredWriter;
    private readonly IStatePersistenceBarrier _persistenceBarrier;

    // BALs whose durable write is still queued: the immutable encoded bytes (the live block's BAL is
    // nulled synchronously). Removed only after the DB write, value-conditionally. Never evicts.
    private readonly ConcurrentDictionary<ValueHash256, PendingBalEntry> _pendingBal = new();
    private readonly Lock _writeLock = new();

    public BlockAccessListStore(
        [KeyFilter(DbNames.BlockAccessLists)] IDb balDb,
        BlockAccessListDecoder? decoder = null,
        IDeferredBlockDataWriter? deferredWriter = null,
        bool deferBal = false,
        IStatePersistenceBarrier? persistenceBarrier = null)
    {
        _balDb = balDb;
        _balDecoder = decoder ?? new();
        _deferredWriter = deferBal && deferredWriter is { Enabled: true } ? deferredWriter : null;
        _persistenceBarrier = persistenceBarrier ?? NullStatePersistenceBarrier.Instance;

        if (_deferredWriter is not null && _persistenceBarrier.IsEnabled)
        {
            _persistenceBarrier.Register(FlushBalUpTo);
        }
    }

    // A plain class (not a record) so value-conditional removal keys on reference identity; the block
    // number lets the barrier flush entries up to a block.
    private sealed class PendingBalEntry(ulong blockNumber, Hash256 blockHash, byte[] rlp)
    {
        public ulong BlockNumber { get; } = blockNumber;
        public Hash256 BlockHash { get; } = blockHash;
        public byte[] Rlp { get; } = rlp;
    }

    [SkipLocalsInit]
    public void Insert(ulong blockNumber, Hash256 blockHash, ReadOnlyBlockAccessList bal)
    {
        using ArrayPoolSpan<byte> rlp = _balDecoder.EncodeToArrayPoolSpan(bal);
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        _balDb.PutSpan(key, rlp);
    }

    [SkipLocalsInit]
    public void Insert(ulong blockNumber, Hash256 blockHash, byte[] encodedBal)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        _balDb.PutSpan(key, encodedBal);
    }

    [SkipLocalsInit]
    public void Insert(ulong blockNumber, Hash256 blockHash, scoped ReadOnlySpan<byte> encodedBal)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        _balDb.PutSpan(key, encodedBal);
    }

    public void InsertFromBlockDeferred(Block block)
    {
        if (_deferredWriter is null)
        {
            ((IBlockAccessListStore)this).InsertFromBlock(block);
            return;
        }

        Hash256 blockHash = block.Hash ?? throw new ArgumentException("Block hash is required to persist a block access list.", nameof(block));

        // Snapshot the encoded bytes now, then free the live BAL immediately - exactly the reclamation
        // InsertFromBlock does; only the database write defers. The pre-encoded array is cloned so a later
        // mutation cannot diverge the deferred write from the header BAL hash (the synchronous path
        // captured the bytes into RocksDB at call time). The BlockAccessList branch already encodes fresh.
        byte[]? rlp = block.EncodedBlockAccessList is { } encoded ? (byte[])encoded.Clone()
            : block.BlockAccessList is { } bal ? BlockAccessListDecoder.EncodeToBytes(bal)
            : null;

        block.GeneratedBlockAccessList = null;
        block.EncodedBlockAccessList = null;

        if (rlp is null) return;

        PendingBalEntry entry = new(block.Number, blockHash, rlp);
        _pendingBal[blockHash.ValueHash256] = entry;
        _deferredWriter.Enqueue(() => PersistDeferred(entry));
    }

    private bool PersistDeferred(PendingBalEntry entry)
    {
        lock (_writeLock)
        {
            // Skip if a Delete (or the gate racing the writer) already dropped this exact entry.
            ValueHash256 key = entry.BlockHash.ValueHash256;
            if (!_pendingBal.TryGetValue(key, out PendingBalEntry? current) || !ReferenceEquals(current, entry))
            {
                return false;
            }

            Insert(entry.BlockNumber, entry.BlockHash, entry.Rlp);
            _pendingBal.TryRemove(new KeyValuePair<ValueHash256, PendingBalEntry>(key, entry));
            return true;
        }
    }

    /// <summary>
    /// Barrier hook: persist any queued BAL write for a block up to <paramref name="blockNumber"/>,
    /// then fsync the BAL DB WAL so it is durable before the block's state is persisted.
    /// </summary>
    /// <remarks>
    /// The fsync is unconditional: the eager writer's writes are WAL-buffered (WriteFlags.None) and
    /// nothing else syncs them, so gating on the gate winning the write would leave the common path unsynced.
    /// </remarks>
    private void FlushBalUpTo(long blockNumber)
    {
        ulong upTo = (ulong)blockNumber;
        foreach (KeyValuePair<ValueHash256, PendingBalEntry> kv in _pendingBal)
        {
            if (kv.Value.BlockNumber <= upTo)
            {
                PersistDeferred(kv.Value);
            }
        }

        _balDb.Flush(onlyWal: true);
    }

    [SkipLocalsInit]
    public MemoryManager<byte>? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        // Non-owning wrapper: the array stays alive via the returned manager, and disposing it must not
        // free the still-pending overlay buffer.
        if (_pendingBal.TryGetValue(blockHash.ValueHash256, out PendingBalEntry? pending))
        {
            return ArrayMemoryManager.From(pending.Rlp);
        }

        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        return _balDb.GetOwnedMemory(key);
    }

    [SkipLocalsInit]
    public bool Exists(ulong blockNumber, Hash256 blockHash)
    {
        if (_pendingBal.ContainsKey(blockHash.ValueHash256)) return true;

        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        return _balDb.KeyExists(key);
    }

    public ReadOnlyBlockAccessList? Get(ulong blockNumber, Hash256 blockHash)
    {
        using MemoryManager<byte>? rlp = GetRlp(blockNumber, blockHash);
        return rlp is null ? null : _balDecoder.Decode(rlp.Memory.Span);
    }

    [SkipLocalsInit]
    public void Delete(ulong blockNumber, Hash256 blockHash)
    {
        lock (_writeLock)
        {
            _pendingBal.TryRemove(blockHash.ValueHash256, out _);

            Span<byte> key = stackalloc byte[KeyLength];
            KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
            _balDb.Remove(key);
        }
    }
}
