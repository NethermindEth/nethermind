// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool : TxDistinctSortedPool
{
    private readonly LruCache<ValueKeccak, Transaction> _blobTxCache = new(256, 256, "blob txs cache");

    public BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
        : base(capacity, comparer, logManager)
    {
    }

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer) => comparer.GetBlobReplacementComparer();

    public override bool TryInsert(ValueKeccak hash, Transaction fullBlobTx, out Transaction? removed)
    {
        Transaction lightBlobTx = new()
        {
            Type = TxType.Blob,
            Nonce = fullBlobTx.Nonce,
            GasLimit = fullBlobTx.GasLimit,
            GasPrice = fullBlobTx.GasPrice, // means MaxPriorityFeePerGas
            DecodedMaxFeePerGas = fullBlobTx.DecodedMaxFeePerGas,
            MaxFeePerDataGas = fullBlobTx.MaxFeePerDataGas,
            Value = fullBlobTx.Value,
            SenderAddress = fullBlobTx.SenderAddress,
            Hash = hash.ToKeccak(),
        };

        if (base.TryInsert(hash, lightBlobTx, out removed))
        {
            _blobTxCache.Set(hash, fullBlobTx);
            // save to db fullBlobTx
            return true;
        }

        return false;
    }

    public IEnumerable<Transaction> GetBlobTransactions()
    {
        // to refactor - it must return starting from the best
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
            // if (!_blobTxCache.TryGet(hash, out fullBlobTx))
            // {
            //     if (_blobTxsDb.TryGet(hash, fullBlobTx))
            //     {
            //         _blobTxCache.Set(hash, fullBlobTx);
            //         return true;
            //     }
            // }
            // return false;

            return _blobTxCache.TryGet(hash, out fullBlobTx);
        }

        fullBlobTx = default;
        return false;
    }

    protected override bool Remove(ValueKeccak hash, Transaction tx)
    {
        _blobTxCache.Delete(hash);
        // delete from db here
        return base.Remove(hash, tx);
    }
}
