// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool : TxDistinctSortedPool
{
    protected const int MaxNumberOfBlobsInBlock = (int)(Eip4844Constants.MaxBlobGasPerBlock / Eip4844Constants.BlobGasPerBlob);

    public BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
        : base(capacity, comparer, logManager)
    {
    }

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public virtual bool TryInsertBlobTx(Transaction fullBlobTx, out Transaction? removed)
        => base.TryInsert(fullBlobTx.Hash, fullBlobTx, out removed);

    public virtual bool TryGetBlobTx(ValueKeccak hash, out Transaction? fullBlobTx)
        => base.TryGetValue(hash, out fullBlobTx);

    public virtual IEnumerable<Transaction> GetBlobTransactions()
    {
        int pickedBlobs = 0;
        List<Transaction>? blobTxsToReadd = null;

        while (pickedBlobs < MaxNumberOfBlobsInBlock)
        {
            Transaction? bestTx = GetFirsts().Min;

            if (bestTx?.Hash is null || bestTx.BlobVersionedHashes is null)
            {
                break;
            }

            if (pickedBlobs + bestTx.BlobVersionedHashes.Length <= MaxNumberOfBlobsInBlock)
            {
                yield return bestTx;
                pickedBlobs += bestTx.BlobVersionedHashes.Length;
            }

            if (TryRemove(bestTx.Hash))
            {
                blobTxsToReadd ??= new(MaxNumberOfBlobsInBlock);
                blobTxsToReadd.Add(bestTx!);
            }
        }

        if (blobTxsToReadd is not null)
        {
            foreach (Transaction blobTx in blobTxsToReadd)
            {
                TryInsert(blobTx.Hash, blobTx!, out Transaction? removed);
            }
        }
    }

    /// <summary>
    /// For tests only - to test sorting
    /// </summary>
    internal void TryGetBlobTxSortingEquivalent(Keccak hash, out Transaction? lightBlobTx)
        => base.TryGetValue(hash, out lightBlobTx);
}
