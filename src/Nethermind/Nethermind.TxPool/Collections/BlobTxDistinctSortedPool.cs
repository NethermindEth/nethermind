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
    private readonly ITxStorage _blobTxStorage;
    private readonly LruCache<ValueKeccak, Transaction> _blobTxCache = new(256, 256, "blob txs cache");

    public BlobTxDistinctSortedPool(ITxStorage blobTxStorage, int capacity, IComparer<Transaction> comparer, ILogManager logManager)
        : base(capacity, comparer, logManager)
    {
        _blobTxStorage = blobTxStorage;
    }

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer) => comparer.GetBlobReplacementComparer();

    public bool TryInsert(Transaction fullBlobTx, out Transaction? removed) =>
        TryInsert(fullBlobTx.Hash!, fullBlobTx, out removed);

    private bool TryInsert(Keccak hash, Transaction fullBlobTx, out Transaction? removed)
    {
        Transaction lightBlobTx = new()
        {
            Type = TxType.Blob,
            Nonce = fullBlobTx.Nonce,
            GasLimit = fullBlobTx.GasLimit,
            GasPrice = fullBlobTx.GasPrice, // means MaxPriorityFeePerGas
            DecodedMaxFeePerGas = fullBlobTx.DecodedMaxFeePerGas,
            MaxFeePerDataGas = fullBlobTx.MaxFeePerDataGas,
            BlobVersionedHashes = new byte[fullBlobTx.BlobVersionedHashes!.Length][],
            Value = fullBlobTx.Value,
            SenderAddress = fullBlobTx.SenderAddress,
            Hash = hash,
            GasBottleneck = fullBlobTx.GasBottleneck,
        };

        if (base.TryInsert(hash, lightBlobTx, out removed))
        {
            _blobTxCache.Set(hash, fullBlobTx);
            _blobTxStorage.Add(fullBlobTx);
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

    protected override bool Remove(ValueKeccak hash, Transaction tx)
    {
        _blobTxCache.Delete(hash);
        _blobTxStorage.Delete(hash);
        return base.Remove(hash, tx);
    }
}
