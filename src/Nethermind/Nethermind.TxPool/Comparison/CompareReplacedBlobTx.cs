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
    public int Compare(Transaction? x, Transaction? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (ReferenceEquals(null, y)) return 1;
        if (ReferenceEquals(null, x)) return -1;

        // do not allow to replace blob tx by the one with lower number of blobs
        if (y.BlobVersionedHashes is null || x.BlobVersionedHashes is null) return 1;
        if (y.BlobVersionedHashes.Length > x.BlobVersionedHashes.Length) return 1;

        // always allow replacement of zero fee txs
        if (y.MaxFeePerGas.IsZero) return -1; //ToDo: do we need it?

        if (y.MaxFeePerGas * 2 > x.MaxFeePerGas) return 1;
        if (y.MaxPriorityFeePerGas * 2 > x.MaxPriorityFeePerGas) return 1;
        if (y.MaxFeePerBlobGas * 2 > x.MaxFeePerBlobGas) return 1;

        // if we are here, it means that all new fees are at least 2x higher than old ones, so replacement is allowed
        return -1;
    }
}
