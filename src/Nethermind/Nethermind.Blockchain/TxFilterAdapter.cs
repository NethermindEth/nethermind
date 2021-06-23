//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;

namespace Nethermind.Blockchain
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

        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            if (tx is not GeneratedTransaction)
            {
                BlockHeader parentHeader = _blockTree.Head?.Header;
                if (parentHeader == null) return (true, null);

                (bool accepted, string? reason) = _txFilter.IsAllowed(tx, parentHeader);
                if (reason is not null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, filtered ({reason}).");
                }
                
                return (accepted, reason is null ? null : AddTxResult.Filtered);
            }

            return (true, null);
        }
    }
}
