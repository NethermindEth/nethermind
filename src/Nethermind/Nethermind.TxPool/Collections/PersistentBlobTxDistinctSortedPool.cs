// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class PersistentBlobTxDistinctSortedPool : BlobTxDistinctSortedPool
{
    private readonly ITxStorage _blobTxStorage;
    private readonly LruCache<ValueHash256, Transaction> _blobTxCache;
    private readonly ILogger _logger;

    public PersistentBlobTxDistinctSortedPool(ITxStorage blobTxStorage, ITxPoolConfig txPoolConfig, IComparer<Transaction> comparer, ILogManager logManager)
        : base(txPoolConfig.PersistentBlobStorageSize, comparer, logManager)
    {
        _blobTxStorage = blobTxStorage ?? throw new ArgumentNullException(nameof(blobTxStorage));
        _blobTxCache = new(txPoolConfig.BlobCacheSize, txPoolConfig.BlobCacheSize, "blob txs cache");
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        RecreateLightTxCollectionAndCache(blobTxStorage);
    }

    private void RecreateLightTxCollectionAndCache(ITxStorage blobTxStorage)
    {
        if (_logger.IsTrace) _logger.Trace("Recreating light collection of blob transactions and cache");
        int numberOfTxsInDb = 0;
        int numberOfBlobsInDb = 0;
        long startTime = Stopwatch.GetTimestamp();
        foreach (LightTransaction lightBlobTx in blobTxStorage.GetAll())
        {
            if (lightBlobTx.SenderAddress is not null
                && base.InsertCore(lightBlobTx.Hash, lightBlobTx, lightBlobTx.SenderAddress))
            {
                numberOfTxsInDb++;
                numberOfBlobsInDb += lightBlobTx.GetBlobCount();
            }
        }

        if (_logger.IsInfo && numberOfTxsInDb != 0)
        {
            long loadingTime = (long)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
            _logger.Info($"Loaded {numberOfTxsInDb} blob txs from persistent db, containing {numberOfBlobsInDb} blobs, in {loadingTime:N0}ms");
            _logger.Info($"There are {BlobIndex.Count} unique blobs indexed");
        }
    }

    protected override bool InsertCore(ValueHash256 hash, Transaction fullBlobTx, AddressAsKey groupKey)
    {
        if (base.InsertCore(hash, new LightTransaction(fullBlobTx), groupKey))
        {
            _blobTxCache.Set(fullBlobTx.Hash, fullBlobTx);
            _blobTxStorage.Add(fullBlobTx);
            return true;
        }

        return false;
    }

    protected override bool TryGetValueNonLocked(ValueHash256 hash, [NotNullWhen(true)] out Transaction? fullBlobTx)
    {
        // Firstly check if tx is present in in-memory collection of light blob txs (without actual blobs).
        // If not, just return false
        if (base.TryGetValueNonLocked(hash, out Transaction? lightTx))
        {
            // tx is present in light collection. Try to get full blob tx from cache
            if (_blobTxCache.TryGet(hash, out fullBlobTx))
            {
                return true;
            }

            // tx is present, but not cached, at this point we need to load it from db...
            if (_blobTxStorage.TryGet(hash, lightTx.SenderAddress!, lightTx.Timestamp, out fullBlobTx))
            {
                // ...and we are saving recently used blob tx to cache
                _blobTxCache.Set(hash, fullBlobTx);
                return true;
            }
        }

        fullBlobTx = default;
        return false;
    }

    public override int TryGetBlobsAndProofsV1(
        byte[][] requestedBlobVersionedHashes,
        byte[]?[] blobs,
        ReadOnlyMemory<byte[]>[] proofs)
    {
        int found = 0;
        int missCount = 0;

        // Rent arrays for Phase 2 (DB lookup of cache misses).
        // Sized to 4x request length to accommodate multiple candidate tx hashes per blob
        // (e.g. when the same blob versioned hash appears in multiple transactions).
        int maxMisses = requestedBlobVersionedHashes.Length * 4;
        TxLookupKey[] dbKeys = ArrayPool<TxLookupKey>.Shared.Rent(maxMisses);
        int[] missOutputIndex = ArrayPool<int>.Shared.Rent(maxMisses);
        int[] missBlobIndex = ArrayPool<int>.Shared.Rent(maxMisses);
        try
        {
            // Phase 1: Under lock — in-memory lookups only
            using (McsLock.Disposable lockRelease = Lock.Acquire())
            {
                for (int i = 0; i < requestedBlobVersionedHashes.Length; i++)
                {
                    byte[] requestedBlobVersionedHash = requestedBlobVersionedHashes[i];
                    if (!BlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes))
                        continue;

                    foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
                    {
                        if (!base.TryGetValueNonLocked(hash, out Transaction? lightTx)
                            || lightTx is not LightTransaction lt
                            || lt.ProofVersion != ProofVersion.V1
                            || lightTx.BlobVersionedHashes is not { Length: > 0 })
                            continue;

                        int indexOfBlob = -1;
                        for (int b = 0; b < lightTx.BlobVersionedHashes.Length; b++)
                        {
                            if (Bytes.AreEqual(lightTx.BlobVersionedHashes[b], requestedBlobVersionedHash))
                            {
                                indexOfBlob = b;
                                break;
                            }
                        }
                        if (indexOfBlob < 0)
                            continue;

                        // Try cache first — on hit, populate and stop searching for this blob hash
                        if (_blobTxCache.TryGet(hash, out Transaction? cachedTx)
                            && cachedTx.NetworkWrapper is ShardBlobNetworkWrapper { Version: ProofVersion.V1 } cachedWrapper)
                        {
                            blobs[i] = cachedWrapper.Blobs[indexOfBlob];
                            proofs[i] = new ReadOnlyMemory<byte[]>(
                                cachedWrapper.Proofs,
                                Ckzg.CellsPerExtBlob * indexOfBlob,
                                Ckzg.CellsPerExtBlob);
                            found++;
                            break;
                        }

                        // Cache miss — record for Phase 2 DB lookup and continue to try
                        // remaining tx hashes so that if this candidate is missing from DB,
                        // later candidates can still satisfy the request.
                        if (missCount < maxMisses)
                        {
                            dbKeys[missCount] = new TxLookupKey(hash, lightTx.SenderAddress!, lightTx.Timestamp);
                            missOutputIndex[missCount] = i;
                            missBlobIndex[missCount] = indexOfBlob;
                            missCount++;
                        }
                    }
                }
            }

            // Phase 2: Outside lock — single RocksDB MultiGet for all misses
            if (missCount > 0)
            {
                Transaction?[] dbResults = ArrayPool<Transaction?>.Shared.Rent(missCount);
                try
                {
                    Array.Clear(dbResults, 0, missCount);
                    _blobTxStorage.TryGetMany(dbKeys, missCount, dbResults);

                    for (int m = 0; m < missCount; m++)
                    {
                        int outIdx = missOutputIndex[m];

                        // Skip if this output slot was already filled by a cache hit or earlier candidate
                        if (blobs[outIdx] is not null)
                            continue;

                        Transaction? fullTx = dbResults[m];
                        if (fullTx?.NetworkWrapper is ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper)
                        {
                            int blobIdx = missBlobIndex[m];
                            blobs[outIdx] = wrapper.Blobs[blobIdx];
                            proofs[outIdx] = new ReadOnlyMemory<byte[]>(
                                wrapper.Proofs,
                                Ckzg.CellsPerExtBlob * blobIdx,
                                Ckzg.CellsPerExtBlob);
                            found++;

                            _blobTxCache.Set(dbKeys[m].Hash, fullTx);
                        }
                    }
                }
                finally
                {
                    ArrayPool<Transaction?>.Shared.Return(dbResults, clearArray: true);
                }
            }

            return found;
        }
        finally
        {
            ArrayPool<TxLookupKey>.Shared.Return(dbKeys, clearArray: true);
            ArrayPool<int>.Shared.Return(missOutputIndex);
            ArrayPool<int>.Shared.Return(missBlobIndex);
        }
    }

    protected override bool Remove(ValueHash256 hash, out Transaction? tx)
    {
        if (base.Remove(hash, out tx))
        {
            if (tx is not null)
            {
                _blobTxStorage.Delete(hash, tx.Timestamp);
            }

            _blobTxCache.Delete(hash);

            return true;
        }

        return false;
    }
}
