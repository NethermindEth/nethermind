// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;

namespace Nethermind.Consensus;

public class TxFilterAdapter(IBlockTree blockTree, ITxFilter txFilter, ILogManager logManager, ISpecProvider specProvider) : IIncomingTxFilter
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (tx is not GeneratedTransaction)
        {
            BlockHeader parentHeader = blockTree.Head?.Header;
            if (parentHeader is null) return AcceptTxResult.Accepted;

            AcceptTxResult isAllowed = txFilter.IsAllowed(tx, parentHeader, specProvider.GetSpec(parentHeader));
            if (!isAllowed)
            {
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, filtered ({isAllowed}).");
            }

            return isAllowed;
        }

        return AcceptTxResult.Accepted;
    }
}
