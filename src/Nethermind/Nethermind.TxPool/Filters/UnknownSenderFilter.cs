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
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions with the sender address not resolved properly.
    /// </summary>
    internal class UnknownSenderFilter : IIncomingTxFilter
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ILogger _logger;

        public UnknownSenderFilter(IEthereumEcdsa ecdsa, ILogger logger)
        {
            _ecdsa = ecdsa;
            _logger = logger;
        }
            
        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            /* We have encountered multiple transactions that do not resolve sender address properly.
             * We need to investigate what these txs are and why the sender address is resolved to null.
             * Then we need to decide whether we really want to broadcast them.
             */
            if (tx.SenderAddress is null)
            {
                tx.SenderAddress = _ecdsa.RecoverAddress(tx);
                if (tx.SenderAddress is null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, no sender.");
                    return (false, AddTxResult.FailedToResolveSender);
                }
            }

            return (true, null);
        }
    }
}
