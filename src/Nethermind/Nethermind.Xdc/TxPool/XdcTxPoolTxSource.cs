// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

internal class XdcTxPoolTxSource(
    ITxPool transactionPool,
    ISpecProvider specProvider,
    ITransactionComparerProvider transactionComparerProvider,
    ILogManager logManager,
    ITxFilterPipeline txFilterPipeline,
    IBlocksConfig blocksConfig)
    : TxPoolTxSource(transactionPool, specProvider, transactionComparerProvider, logManager, txFilterPipeline, blocksConfig)
{
    private readonly ITxPool _xdcTransactionPool = transactionPool;
    private readonly ISpecProvider _xdcSpecProvider = specProvider;

    protected override IDictionary<AddressAsKey, Transaction[]> GetPendingTransactions(BlockHeader parent, bool filterSource, UInt256 baseFee)
    {
        IDictionary<AddressAsKey, Transaction[]> filtered = base.GetPendingTransactions(parent, filterSource, baseFee);

        if (!filterSource)
            return filtered;

        IXdcReleaseSpec spec = _xdcSpecProvider.GetXdcSpec(parent.Number + 1);

        // Special transactions have GasPrice=0 and are excluded by the base fee filter in
        // GetPendingTransactionsBySender. Fetch unfiltered and merge back sender buckets
        // that were dropped but lead with a special transaction.
        IDictionary<AddressAsKey, Transaction[]> all = _xdcTransactionPool.GetPendingTransactionsBySender();

        foreach (KeyValuePair<AddressAsKey, Transaction[]> bucket in all)
        {
            if (!filtered.ContainsKey(bucket.Key) && bucket.Value.Length > 0 && bucket.Value[0].IsSpecialTransaction(spec))
                filtered[bucket.Key] = bucket.Value;
        }

        return filtered;
    }
}
