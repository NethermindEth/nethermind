// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
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

    private readonly record struct DbLoadItem(int ResultIndex, ValueHash256 TxHash, Transaction LightTx, int BlobIndex);

    protected override void TryGetBlobsAndProofsCore<TResult>(
        byte[][] requestedBlobVersionedHashes,
        ArrayPoolList<TResult?> results,
        ProofVersion requiredVersion,
        Func<ShardBlobNetworkWrapper, int, TResult> createResult)
        where TResult : struct
    {
        int length = requestedBlobVersionedHashes.Length;

        using ArrayPoolList<DbLoadItem> dbLoadItems = new(length);
        using (Lock.Acquire())
        {
            for (int i = 0; i < length; i++)
            {
                byte[] blobHash = requestedBlobVersionedHashes[i];

                bool found = false;
                if (BlobIndex.TryGetValue(blobHash, out List<Hash256>? txHashes))
                {
                    foreach (Hash256 txHash in CollectionsMarshal.AsSpan(txHashes))
                    {
                        if (base.TryGetValueNonLocked(txHash, out Transaction? lightTx)
                            && lightTx.BlobVersionedHashes is not null)
                        {
                            int blobIndex = FindBlobIndex(lightTx.BlobVersionedHashes!, blobHash);
                            if (blobIndex >= 0)
                            {
                                if (_blobTxCache.TryGet(txHash, out Transaction fullTx)
                                    && fullTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
                                    && wrapper.Version == requiredVersion)
                                {
                                    results.Add(createResult(wrapper, blobIndex));
                                    found = true;
                                }
                                else
                                {
                                    dbLoadItems.Add(new DbLoadItem(i, txHash, lightTx, blobIndex));
                                }

                                break;
                            }
                        }
                    }
                }

                if (!found)
                {
                    results.Add(null);
                }
            }
        }

        int dbLoadCount = dbLoadItems.Count;
        if (dbLoadCount > 0)
        {
            Parallel.For(0, dbLoadCount, j =>
            {
                DbLoadItem item = dbLoadItems[j];

                if (_blobTxStorage.TryGet(item.TxHash, item.LightTx.SenderAddress!, item.LightTx.Timestamp, out Transaction? fullTx))
                {
                    _blobTxCache.Set(item.TxHash, fullTx);
                    if (fullTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper && wrapper.Version == requiredVersion)
                    {
                        results[item.ResultIndex] = createResult(wrapper, item.BlobIndex);
                    }
                }
            });
        }
    }
}
