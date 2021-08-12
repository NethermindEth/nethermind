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
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Ignores transactions that outright exceed block gas limit or configured max block gas limit.
    /// </summary>
    internal class GasLimitTxFilter : IIncomingTxFilter
    {
        private readonly IChainHeadInfoProvider _chainHeadInfoProvider;
        private readonly ILogger _logger;
        private readonly long _configuredGasLimit;
        
        public GasLimitTxFilter(IChainHeadInfoProvider chainHeadInfoProvider, ITxPoolConfig txPoolConfig,
            ILogger logger)
        {
            _chainHeadInfoProvider = chainHeadInfoProvider;
            _logger = logger;
            _configuredGasLimit = txPoolConfig.GasLimit ?? long.MaxValue;
        }
            
        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            long gasLimit = Math.Min(_chainHeadInfoProvider.BlockGasLimit ?? long.MaxValue, _configuredGasLimit);
            if (tx.GasLimit > gasLimit)
            {
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, gas limit exceeded.");
                return (false, AddTxResult.GasLimitExceeded);
            }

            return (true, null);
        }
    }
}
