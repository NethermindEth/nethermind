// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison;

/// <summary>
/// Compare fee of newcomer blob transaction with fee of transaction intended to be replaced
/// </summary>
public class CompareReplacedBlobTx : IComparer<Transaction?>
{
    public static readonly CompareReplacedBlobTx Instance = new();

    private CompareReplacedBlobTx() { }

    // To replace old blob transaction, new transaction needs to have fee at least 2x higher than current fee.
    // 2x higher must be MaxPriorityFeePerGas, MaxFeePerGas and MaxFeePerDataGas
    public int Compare(Transaction? newTx, Transaction? oldTx)
    {
        if (ReferenceEquals(newTx, oldTx)) return TxComparisonResult.NotDecided;
        if (ReferenceEquals(null, oldTx)) return TxComparisonResult.KeepOld;
        if (ReferenceEquals(null, newTx)) return TxComparisonResult.TakeNew;

        // do not allow to replace blob tx by the one with lower number of blobs
        if (oldTx.BlobVersionedHashes is null || newTx.BlobVersionedHashes is null) return TxComparisonResult.KeepOld;
        if (oldTx.BlobVersionedHashes.Length > newTx.BlobVersionedHashes.Length) return TxComparisonResult.KeepOld;

        if (oldTx.MaxFeePerGas * 2 > newTx.MaxFeePerGas) return TxComparisonResult.KeepOld;
        if (oldTx.MaxPriorityFeePerGas * 2 > newTx.MaxPriorityFeePerGas) return TxComparisonResult.KeepOld;
        if (oldTx.MaxFeePerBlobGas * 2 > newTx.MaxFeePerBlobGas) return TxComparisonResult.KeepOld;

        // if we are here, it means that all new fees are at least 2x higher than old ones, so replacement is allowed
        return TxComparisonResult.TakeNew;
    }
}
