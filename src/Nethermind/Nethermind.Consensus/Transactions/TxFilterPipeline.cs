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
using System.Linq;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Transactions
{
    public class TxFilterPipeline : ITxFilterPipeline
    {
        private readonly List<ITxFilter> _filters;
        private readonly ILogger _logger;

        public TxFilterPipeline(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _filters = new List<ITxFilter>();
        }
        
        public void AddTxFilter(ITxFilter txFilter)
        {
            _filters.Add(txFilter);
        }

        public bool Execute(Transaction tx, BlockHeader parentHeader)
        {
            if (!_filters.Any())
            {
                return true;
            }
            
            foreach (ITxFilter filter in _filters)
            {
                var result = filter.IsAllowed(tx, parentHeader);
                if ( !result.Allowed)
                {
                    if (_logger.IsDebug) _logger.Debug($"Rejected tx ({result.Reason}) {tx.ToShortString()}");
                    return false;
                }
            }

            return true;
        }
    }
}
