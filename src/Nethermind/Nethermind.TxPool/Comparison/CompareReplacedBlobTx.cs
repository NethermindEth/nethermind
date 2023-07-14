// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison;

public class CompareReplacedBlobTx : IComparer<Transaction?>
{
    public static readonly CompareReplacedBlobTx Instance = new();

    public CompareReplacedBlobTx() { }

    // To replace old blob transaction, new transaction needs to have fee at least 2x higher than current fee.
    // 2x higher must be MaxPriorityFeePerGas, MaxFeePerGas and MaxFeePerDataGas
    public int Compare(Transaction? x, Transaction? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (ReferenceEquals(null, y)) return 1;
        if (ReferenceEquals(null, x)) return -1;

        // always allow replacement of zero fee txs
        if (y.MaxFeePerGas.IsZero) return -1; //ToDo: do we need it?

        // ToDo: handle overflows
        if (y.MaxFeePerGas * 2 > x.MaxFeePerGas) return 1;
        if (y.MaxPriorityFeePerGas * 2 > x.MaxPriorityFeePerGas) return 1;
        if (y.MaxFeePerDataGas * 2 > x.MaxFeePerDataGas) return 1;

        // if we are here, it means that all new fees are at least 2x higher than old ones, so replacement is allowed
        return -1;
    }
}
