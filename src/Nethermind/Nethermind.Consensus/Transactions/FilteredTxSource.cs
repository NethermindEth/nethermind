// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions;

public class FilteredTxSource<T>(ITxSource innerSource, ITxFilter txFilter, ILogManager logManager, ISpecProvider specProvider, IBlocksConfig? blocksConfig) : ITxSource where T : Transaction
{
    private readonly ILogger _logger = logManager?.GetClassLogger<FilteredTxSource<T>>() ?? throw new ArgumentNullException(nameof(logManager));

    public bool SupportsBlobs => innerSource.SupportsBlobs;

    public IEnumerable<Transaction> GetTransactions(BlockHeader parentHeader, long gasLimit, PayloadAttributes? payloadAttributes, bool filterSource)
    {
        IReleaseSpec currentSpec = NextBlockSpecHelper.GetSpec(specProvider, parentHeader, payloadAttributes, blocksConfig);

        foreach (Transaction tx in innerSource.GetTransactions(parentHeader, gasLimit, payloadAttributes, filterSource))
        {
            if (tx is T)
            {
                AcceptTxResult acceptTxResult = txFilter.IsAllowed(tx, parentHeader, currentSpec);
                if (acceptTxResult)
                {
                    if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be included in block.");
                    yield return tx;
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejecting ({acceptTxResult}) {tx.ToShortString()}");
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be included in block, skipped validation for {tx.GetType()}.");
                yield return tx;
            }
        }
    }

    public override string ToString() => $"{nameof(FilteredTxSource<T>)} [ {innerSource} ]";
}
