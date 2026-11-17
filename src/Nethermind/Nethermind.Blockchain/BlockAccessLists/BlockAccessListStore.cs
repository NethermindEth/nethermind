// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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

    /// <inheritdoc/>
    public Lock WriteLock { get; } = new();

    private readonly IDb _balDb;
    private readonly BlockAccessListDecoder _balDecoder;
    // Like a block body, a lost BAL cannot be regenerated, so the barrier must make it durable before state. Null when off.
    private readonly DeferredWriteOverlay<byte[]>? _pending;

    public BlockAccessListStore(
        [KeyFilter(DbNames.BlockAccessLists)] IDb balDb,
        BlockAccessListDecoder? decoder = null,
        IDeferredBlockDataWriter? deferredWriter = null,
        IStatePersistenceBarrier? persistenceBarrier = null)
    {
        _balDb = balDb;
        _balDecoder = decoder ?? new();

        if (deferredWriter is { Enabled: true })
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

        // Reuse the block's owned encoding. The fallback must also be owned because the overlay and its
        // non-owning read views can outlive this call, so a pool-rented buffer could be returned too early.
        byte[]? rlp = block.EncodedBlockAccessList is { } encoded ? encoded
            : block.BlockAccessList is { } bal && !Exists(block.Number, blockHash) ? BlockAccessListDecoder.EncodeToBytes(bal)
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

    /// <inheritdoc/>
    /// <remarks>Reads the lists that are not pending in one batch.</remarks>
    public void AppendRlps(ReadOnlySpan<ulong> blockNumbers, ReadOnlySpan<ValueHash256> blockHashes, ArrayPoolList<byte> destination, Span<Range?> ranges)
    {
        using ArrayPoolSpan<byte> keys = new(blockHashes.Length * KeyLength);
        using ArrayPoolSpan<int> stored = new(blockHashes.Length);
        int count = 0;
        for (int i = 0; i < blockHashes.Length; i++)
        {
            if (_pending is not null && _pending.TryGet(in blockHashes[i], out byte[] pendingRlp))
            {
                ranges[i] = new AppendingDecoder(destination).Decode(pendingRlp);
                continue;
            }

            KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumbers[i], blockHashes[i], keys.Slice(count * KeyLength, KeyLength));
            stored[count++] = i;
        }

        using ArrayPoolSpan<Range?> storedRanges = new(count);
        _balDb.GetMany<Range?, AppendingDecoder>(keys.Slice(0, count * KeyLength), KeyLength, storedRanges, new(destination));
        for (int j = 0; j < count; j++)
        {
            ranges[stored[j]] = storedRanges[j];
        }
    }

    private readonly struct AppendingDecoder(ArrayPoolList<byte> destination) : IValueDecoder<Range?>
    {
        public Range? Decode(ReadOnlySpan<byte> buffer)
        {
            int start = destination.Count;
            destination.AddRange(buffer);
            return new Range(start, destination.Count);
        }
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
        return rlp is null ? null : _balDecoder.Decode(rlp.Memory.Span, RlpBehaviors.TrustedInput);
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

    /// <summary>Drops every access list in <c>[fromInclusive, toExclusive)</c> in one operation, draining the
    /// deferred overlay for the range under its own lock so a queued write cannot resurrect one.</summary>
    public void DeleteRange(ulong fromInclusive, ulong toExclusive)
    {
        lock (WriteLock)
        {
            if (_pending is not null)
            {
                _pending.RemoveRange(fromInclusive, toExclusive, () => _balDb.DeleteBlockNumberRange(fromInclusive, toExclusive));
            }
            else
            {
                _balDb.DeleteBlockNumberRange(fromInclusive, toExclusive);
            }
        }

        _balDb.ReclaimBlockNumberRange(fromInclusive, toExclusive);
    }

    [SkipLocalsInit]
    private void Remove(ulong blockNumber, Hash256 blockHash)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        _balDb.Remove(key);
    }
}
