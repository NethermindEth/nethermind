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

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions that are not well formed (not conforming with the yellowpaper and EIPs)
    /// </summary>
    internal class MalformedTxFilter : IIncomingTxFilter
    {
        private readonly ITxValidator _txValidator;
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly ILogger _logger;

        public MalformedTxFilter(IChainHeadSpecProvider specProvider, ITxValidator txValidator, ILogger logger)
        {
            _txValidator = txValidator;
            _specProvider = specProvider;
            _logger = logger;
        }
            
        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = _specProvider.GetSpec();
            if (!_txValidator.IsWellFormed(tx, spec))
            {
                // It may happen that other nodes send us transactions that were signed for another chain or don't have enough gas.
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, invalid transaction.");
                return (false, AddTxResult.Invalid);
            }

            return (true, null);
        }
    }
}
