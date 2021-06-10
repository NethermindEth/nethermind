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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.TxPool
{
    public class FilteredTxPool : TxPool
    {
        private readonly ITxPoolFilter _txPoolFilter;

        public interface ITxPoolFilter
        {
            (bool Accepted, string Reason) Accept(Transaction tx);
        }
        
        public FilteredTxPool(
            ITxStorage txStorage, 
            IEthereumEcdsa ecdsa,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ITxPoolConfig txPoolConfig, 
            ITxValidator txValidator,
            ILogManager logManager, 
            IComparer<Transaction> comparer,
            ITxPoolFilter txPoolFilter = null) 
            : base(txStorage, ecdsa, chainHeadInfoProvider, txPoolConfig, txValidator, logManager, comparer)
        {
            _txPoolFilter = txPoolFilter;
        }

        protected override AddTxResult? FilterTransaction(Transaction tx, in bool managedNonce, in bool isReorg)
        {
            AddTxResult? addTxResult = base.FilterTransaction(tx, in managedNonce, isReorg);
            if (addTxResult == null && !(tx is GeneratedTransaction))
            {
                var result = _txPoolFilter?.Accept(tx);
                if (result != null && !result.Value.Accepted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, filtered ({result.Value.Reason}).");
                    return AddTxResult.Filtered;
                }
            }

            return addTxResult;
        }
    }
}
