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
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Transactions
{
    public class FilteredTxSource<T> : ITxSource where T : Transaction
    {
        private readonly ITxSource _innerSource;
        private readonly ITxFilter _txFilter;
        private readonly ILogger _logger;

        public FilteredTxSource(ITxSource innerSource, ITxFilter txFilter, ILogManager logManager)
        {
            _innerSource = innerSource;
            _txFilter = txFilter;
            _logger = logManager?.GetClassLogger<FilteredTxSource<T>>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            foreach (Transaction tx in _innerSource.GetTransactions(parent, gasLimit))
            {
                if (tx is T)
                {
                    (bool allowed, string reason) = _txFilter.IsAllowed(tx, parent);
                    if (allowed)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be included in block.");
                        yield return tx;
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Rejecting ({reason}) {tx.ToShortString()}");
                    }
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be included in block, skipped validation for {tx.GetType()}.");
                    yield return tx;
                }
            }
        }

        public override string ToString() => $"{nameof(FilteredTxSource<T>)} [ {_innerSource} ]";
    }
}
