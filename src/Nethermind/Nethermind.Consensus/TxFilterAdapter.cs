// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;

namespace Nethermind.Consensus
{
    public class TxFilterAdapter : IIncomingTxFilter
    {
        private readonly ITxFilter _txFilter;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;

        public TxFilterAdapter(IBlockTree blockTree, ITxFilter txFilter, ILogManager logManager, ISpecProvider specProvider)
        {
            _txFilter = txFilter ?? throw new ArgumentNullException(nameof(txFilter));
            _specProvider = specProvider;
            _logger = logManager.GetClassLogger();
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            if (tx is not GeneratedTransaction)
            {
                BlockHeader parentHeader = _blockTree.Head?.Header;
                if (parentHeader is null) return AcceptTxResult.Accepted;

                AcceptTxResult isAllowed = _txFilter.IsAllowed(tx, parentHeader, _specProvider.GetSpec(parentHeader));
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
