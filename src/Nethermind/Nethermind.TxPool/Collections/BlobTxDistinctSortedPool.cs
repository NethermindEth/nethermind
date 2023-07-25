// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool : TxDistinctSortedPool
{
    const int MaxNumberOfBlobsInBlock = (int)(Eip4844Constants.MaxDataGasPerBlock / Eip4844Constants.DataGasPerBlob);
    private readonly ITxStorage _blobTxStorage;
    private readonly LruCache<ValueKeccak, Transaction> _blobTxCache;
    private readonly ILogger _logger;

    public BlobTxDistinctSortedPool(ITxStorage blobTxStorage, ITxPoolConfig txPoolConfig, IComparer<Transaction> comparer, ILogManager logManager)
        : base(txPoolConfig.BlobPoolSize, comparer, logManager)
    {
        _blobTxStorage = blobTxStorage ?? throw new ArgumentNullException(nameof(blobTxStorage));
        _blobTxCache = new(txPoolConfig.BlobCacheSize, txPoolConfig.BlobCacheSize, "blob txs cache");
        _logger = logManager.GetClassLogger();

        RecreateLightTxCollectionAndCache(blobTxStorage);
    }

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    private void RecreateLightTxCollectionAndCache(ITxStorage blobTxStorage)
    {
        if (_logger.IsDebug) _logger.Debug("Recreating light collection of blob transactions and cache");
        foreach (Transaction fullBlobTx in blobTxStorage.GetAll())
        {
            if (base.TryInsert(fullBlobTx.Hash, new LightTransaction(fullBlobTx)))
            {
                _blobTxCache.Set(fullBlobTx.Hash, fullBlobTx);
            }
        }
    }

    public bool TryInsert(Transaction fullBlobTx, out Transaction? removed)
    {
        if (base.TryInsert(fullBlobTx.Hash, new LightTransaction(fullBlobTx), out removed))
        {
            _blobTxCache.Set(fullBlobTx.Hash, fullBlobTx);
            _blobTxStorage.Add(fullBlobTx);
            return true;
        }

        return false;
    }

    // ToDo: add synchronized?
    public IEnumerable<Transaction> GetBlobTransactions()
    {
        int pickedBlobs = 0;
        List<Transaction>? blobTxsToReadd = null;

        while (pickedBlobs < MaxNumberOfBlobsInBlock)
        {
            Transaction? bestTxLight = GetFirsts().Min;

            if (bestTxLight?.Hash is null || bestTxLight.BlobVersionedHashes is null)
            {
                break;
            }

            if (TryGetValue(bestTxLight.Hash, out Transaction? fullBlobTx) && pickedBlobs + bestTxLight.BlobVersionedHashes.Length <= MaxNumberOfBlobsInBlock)
            {
                yield return fullBlobTx!;
                pickedBlobs++;
            }

            if (TryRemove(bestTxLight.Hash))
            {
                blobTxsToReadd ??= new(MaxNumberOfBlobsInBlock);
                blobTxsToReadd.Add(fullBlobTx!);
            }
        }


        if (blobTxsToReadd is not null)
        {
            foreach (Transaction fullBlobTx in blobTxsToReadd)
            {
                TryInsert(fullBlobTx!, out Transaction? removed);
            }
        }

    }

    public bool TryGetValue(Keccak hash, out Transaction? fullBlobTx)
    {
        if (base.TryGetValue(hash, out Transaction? lightBlobTx))
        {
            if (_blobTxCache.TryGet(hash, out fullBlobTx))
            {
                return true;
            }

            if (_blobTxStorage.TryGet(hash, out fullBlobTx))
            {
                _blobTxCache.Set(hash, fullBlobTx!);
                return true;
            }
        }

        fullBlobTx = default;
        return false;
    }

    /// <summary>
    /// For tests only - to test sorting
    /// </summary>
    internal bool TryGetLightValue(Keccak hash, out Transaction? lightBlobTx)
        => base.TryGetValue(hash, out lightBlobTx);

    protected override bool Remove(ValueKeccak hash, Transaction tx)
    {
        _blobTxCache.Delete(hash);
        _blobTxStorage.Delete(hash);
        return base.Remove(hash, tx);
    }
}
