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

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer) => comparer.GetBlobReplacementComparer();

    private void RecreateLightTxCollectionAndCache(ITxStorage blobTxStorage)
    {
        if (_logger.IsDebug) _logger.Debug("Recreating light collection of blob transactions and cache");
        foreach (Transaction fullBlobTx in blobTxStorage.GetAll())
        {
            if (base.TryInsert(fullBlobTx.Hash, CreateLightTx(fullBlobTx)))
            {
                _blobTxCache.Set(fullBlobTx.Hash, fullBlobTx);
            }
        }
    }

    public bool TryInsert(Transaction fullBlobTx, out Transaction? removed)
    {
        if (base.TryInsert(fullBlobTx.Hash, CreateLightTx(fullBlobTx), out removed))
        {
            _blobTxCache.Set(fullBlobTx.Hash, fullBlobTx);
            _blobTxStorage.Add(fullBlobTx);
            return true;
        }

        return false;
    }

    private Transaction CreateLightTx(Transaction fullBlobTx)
    {
        return new Transaction
        {
            Type = TxType.Blob,
            Hash = fullBlobTx.Hash,
            SenderAddress = fullBlobTx.SenderAddress,
            Nonce = fullBlobTx.Nonce,
            Value = fullBlobTx.Value,
            GasLimit = fullBlobTx.GasLimit,
            GasPrice = fullBlobTx.GasPrice, // means MaxPriorityFeePerGas
            DecodedMaxFeePerGas = fullBlobTx.DecodedMaxFeePerGas,
            MaxFeePerDataGas = fullBlobTx.MaxFeePerDataGas,
            BlobVersionedHashes = new byte[fullBlobTx.BlobVersionedHashes!.Length][],
            GasBottleneck = fullBlobTx.GasBottleneck,
        };
    }

    public IEnumerable<Transaction> GetBlobTransactions()
    {
        // ToDo: to refactor - it must lazy enumerate starting from the best
        foreach (Transaction lightBlobTx in GetSnapshot())
        {
            TryGetValue(lightBlobTx.Hash!, out Transaction? fullBlobTx);
            yield return fullBlobTx!;
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

    // only for tests - to test sorting
    internal bool TryGetLightValue(Keccak hash, out Transaction? lightBlobTx)
        => base.TryGetValue(hash, out lightBlobTx);

    protected override bool Remove(ValueKeccak hash, Transaction tx)
    {
        _blobTxCache.Delete(hash);
        _blobTxStorage.Delete(hash);
        return base.Remove(hash, tx);
    }
}
