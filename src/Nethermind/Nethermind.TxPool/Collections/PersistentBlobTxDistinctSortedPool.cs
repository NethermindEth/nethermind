// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class PersistentBlobTxDistinctSortedPool : BlobTxDistinctSortedPool
{
    private readonly ITxStorage _blobTxStorage;
    private readonly LruCache<ValueKeccak, Transaction> _blobTxCache;
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
        if (_logger.IsDebug) _logger.Debug("Recreating light collection of blob transactions and cache");
        int numberOfTxsInDb = 0;
        int numberOfBlobsInDb = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (LightTransaction lightBlobTx in blobTxStorage.GetAll())
        {
            if (base.TryInsert(lightBlobTx.Hash, lightBlobTx, out _))
            {
                numberOfTxsInDb++;
                numberOfBlobsInDb += lightBlobTx.BlobVersionedHashes?.Length ?? 0;
            }
        }

        if (_logger.IsInfo && numberOfTxsInDb != 0)
        {
            long loadingTime = stopwatch.ElapsedMilliseconds;
            _logger.Info($"Loaded {numberOfTxsInDb} blob txs from persistent db, containing {numberOfBlobsInDb} blobs, in {loadingTime}ms");
        }
        stopwatch.Stop();
    }

    public override bool TryInsert(ValueKeccak hash, Transaction fullBlobTx, out Transaction? removed)
    {
        if (base.TryInsert(fullBlobTx.Hash, new LightTransaction(fullBlobTx), out removed))
        {
            _blobTxCache.Set(fullBlobTx.Hash, fullBlobTx);
            _blobTxStorage.Add(fullBlobTx);
            return true;
        }

        return false;
    }

    public override bool TryGetValue(ValueKeccak hash, [NotNullWhen(true)] out Transaction? fullBlobTx)
    {
        if (base.ContainsKey(hash))
        {
            if (_blobTxCache.TryGet(hash, out fullBlobTx))
            {
                return true;
            }

            if (_blobTxStorage.TryGet(hash, out fullBlobTx))
            {
                _blobTxCache.Set(hash, fullBlobTx);
                return true;
            }
        }

        fullBlobTx = default;
        return false;
    }

    protected override bool Remove(ValueKeccak hash, Transaction tx)
    {
        _blobTxCache.Delete(hash);
        _blobTxStorage.Delete(hash);
        return base.Remove(hash, tx);
    }

    public override void VerifyCapacity()
    {
        base.VerifyCapacity();

        if (_logger.IsDebug && Count == _poolCapacity)
            _logger.Debug($"Blob persistent storage has reached max size of {_poolCapacity}, blob txs can be evicted now");
    }
}
