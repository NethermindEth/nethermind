// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
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
    // Like a block body, a lost BAL cannot be regenerated, so deferral is opt-in and coupled to body deferral. Null when off.
    private readonly DeferredWriteOverlay<byte[]>? _pending;

    public BlockAccessListStore(
        [KeyFilter(DbNames.BlockAccessLists)] IDb balDb,
        BlockAccessListDecoder? decoder = null,
        IDeferredBlockDataWriter? deferredWriter = null,
        bool deferBal = false,
        IStatePersistenceBarrier? persistenceBarrier = null)
    {
        _balDb = balDb;
        _balDecoder = decoder ?? new();

        if (deferBal && deferredWriter is { Enabled: true })
        {
            _pending = new DeferredWriteOverlay<byte[]>(deferredWriter, (number, hash, rlp) => Insert(number, hash, rlp));
            (persistenceBarrier ?? NullStatePersistenceBarrier.Instance).RegisterFlush(() => _balDb.Flush(onlyWal: true));
        }
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
        if (_pending is null)
        {
            ((IBlockAccessListStore)this).InsertFromBlock(block);
            return;
        }

        Hash256 blockHash = block.Hash ?? throw new ArgumentException("Block hash is required to persist a block access list.", nameof(block));

        // Retain the immutable encoded bytes and free the live BAL (as InsertFromBlock does); only the DB write
        // defers. Clearing the block property does not affect the array retained by the overlay.
        byte[]? rlp = block.EncodedBlockAccessList is { } encoded ? encoded
            : block.BlockAccessList is { } bal ? BlockAccessListDecoder.EncodeToBytes(bal)
            : null;

        block.GeneratedBlockAccessList = null;
        block.EncodedBlockAccessList = null;

        if (rlp is null) return;

        _pending.Publish(block.Number, blockHash, rlp);
    }

    [SkipLocalsInit]
    public MemoryManager<byte>? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        // Non-owning wrapper: disposing the returned manager must not free the still-pending overlay buffer.
        if (_pending is not null && _pending.TryGet(blockHash, out byte[] pendingRlp))
        {
            return ArrayMemoryManager.From(pendingRlp);
        }

        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        return _balDb.GetOwnedMemory(key);
    }

    [SkipLocalsInit]
    public bool Exists(ulong blockNumber, Hash256 blockHash)
    {
        if (_pending?.Contains(blockHash) == true) return true;

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
        if (_pending is not null)
        {
            _pending.Remove(blockHash, () => Remove(blockNumber, blockHash));
        }
        else
        {
            Remove(blockNumber, blockHash);
        }
    }

    [SkipLocalsInit]
    private void Remove(ulong blockNumber, Hash256 blockHash)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        _balDb.Remove(key);
    }
}
