// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Blockchain.Blocks;

/// <inheritdoc/>
public class DependencyProofStore : IDependencyProofStore
{
    private const int KeyLength = 40;

    private readonly IDb _proofDb;
    // Like a block body, a lost proof cannot be regenerated, so the barrier must make it durable before state.
    private readonly DeferredWriteOverlay<byte[]>? _pending;

    public DependencyProofStore(
        [KeyFilter(DbNames.DependencyProofs)] IDb proofDb,
        IDeferredBlockDataWriter? deferredWriter = null,
        IStatePersistenceBarrier? persistenceBarrier = null)
    {
        _proofDb = proofDb;

        if (deferredWriter is { Enabled: true })
        {
            _pending = new DeferredWriteOverlay<byte[]>(deferredWriter, Insert);
            (persistenceBarrier ?? NullStatePersistenceBarrier.Instance).RegisterFlush(() => _proofDb.Flush(onlyWal: true));
        }
    }

    [SkipLocalsInit]
    public void Insert(ulong blockNumber, Hash256 blockHash, byte[] proof)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        _proofDb.PutSpan(key, proof);
    }

    public void InsertDeferred(ulong blockNumber, Hash256 blockHash, byte[] proof)
    {
        if (_pending is null)
        {
            Insert(blockNumber, blockHash, proof);
            return;
        }

        _pending.Publish(blockNumber, blockHash, proof);
    }

    [SkipLocalsInit]
    public byte[]? Get(ulong blockNumber, Hash256 blockHash)
    {
        if (_pending is not null && _pending.TryGet(blockHash, out byte[] pendingProof)) return pendingProof;

        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        return _proofDb.Get(key);
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

    /// <inheritdoc/>
    /// <remarks>Drains the deferred overlay for the range first, so a queued write cannot resurrect a proof.</remarks>
    public void DeleteRange(ulong fromInclusive, ulong toExclusive)
    {
        if (_pending is not null)
        {
            _pending.RemoveRange(fromInclusive, toExclusive, () => _proofDb.DeleteBlockNumberRange(fromInclusive, toExclusive));
        }
        else
        {
            _proofDb.DeleteBlockNumberRange(fromInclusive, toExclusive);
        }

        _proofDb.ReclaimBlockNumberRange(fromInclusive, toExclusive);
    }

    [SkipLocalsInit]
    private void Remove(ulong blockNumber, Hash256 blockHash)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        _proofDb.Remove(key);
    }
}
