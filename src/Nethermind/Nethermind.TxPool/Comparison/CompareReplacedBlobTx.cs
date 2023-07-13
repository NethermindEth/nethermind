// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool.Comparison;

public class CompareReplacedBlobTx : IComparer<Transaction?>
{
    public static readonly CompareReplacedBlobTx Instance = new();

    public CompareReplacedBlobTx() { }


    //ToDo: it's copy-pasted std comparer, need to be adjusted for a needs of blobs


    // To replace old transaction, new transaction needs to have fee higher by at least 10% (1/10) of current fee.
    // It is required to avoid acceptance and propagation of transaction with almost the same fee as replaced one.
    private const int PartOfFeeRequiredToIncrease = 10;

    public int Compare(Transaction? x, Transaction? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (ReferenceEquals(null, y)) return 1;
        if (ReferenceEquals(null, x)) return -1;

        // always allow replacement of zero fee txs (in legacy txs MaxFeePerGas equals GasPrice)
        if (y.MaxFeePerGas.IsZero) return -1;

        if (!x.Supports1559 && !y.Supports1559)
        {
            y.GasPrice.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpGasPrice);
            int gasPriceResult = (y.GasPrice + bumpGasPrice).CompareTo(x.GasPrice);
            // return -1 (replacement accepted) if fee bump is exactly by PartOfFeeRequiredToIncrease
            // never return 0 - it's allowed or not
            return gasPriceResult != 0 ? gasPriceResult : bumpGasPrice > 0 ? -1 : 1;
        }

        /* MaxFeePerGas for legacy will be GasPrice and MaxPriorityFeePerGas will be GasPrice too
        so we can compare legacy txs without any problems */
        y.MaxFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxFeePerGas);
        if (y.MaxFeePerGas + bumpMaxFeePerGas > x.MaxFeePerGas) return 1;

        y.MaxPriorityFeePerGas.Divide(PartOfFeeRequiredToIncrease, out UInt256 bumpMaxPriorityFeePerGas);
        int result = (y.MaxPriorityFeePerGas + bumpMaxPriorityFeePerGas).CompareTo(x.MaxPriorityFeePerGas);
        // return -1 (replacement accepted) if fee bump is exactly by PartOfFeeRequiredToIncrease
        // never return 0 - it's allowed or not
        return result != 0 ? result : (bumpMaxFeePerGas > 0 && bumpMaxPriorityFeePerGas > 0) ? -1 : 1;
    }
}
