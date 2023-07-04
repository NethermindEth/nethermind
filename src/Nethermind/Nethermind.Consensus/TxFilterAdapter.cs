// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;

namespace Nethermind.Consensus
{
    public class TxFilterAdapter : IIncomingTxFilter
    {
        private readonly ITxFilter _txFilter;
        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;

        public TxFilterAdapter(IBlockTree blockTree, ITxFilter txFilter, ILogManager logManager)
        {
            _txFilter = txFilter ?? throw new ArgumentNullException(nameof(txFilter));
            _logger = logManager.GetClassLogger();
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            if (tx is not GeneratedTransaction)
            {
                BlockHeader parentHeader = _blockTree.Head?.Header;
                if (parentHeader is null) return AcceptTxResult.Accepted;

                AcceptTxResult isAllowed = _txFilter.IsAllowed(tx, parentHeader);
                if (!isAllowed)
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, filtered ({isAllowed}).");
                }

                return isAllowed;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
