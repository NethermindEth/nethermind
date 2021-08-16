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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions with nonces set too far in the future.
    /// Without this filter it would be possible to fill in TX pool with transactions that have low chance of being executed soon.
    /// </summary>
    internal class TooFarNonceFilter : IIncomingTxFilter
    {
        private readonly TxDistinctSortedPool _txs;
        private readonly IAccountStateProvider _accounts;
        private readonly ILogger _logger;
        private readonly uint _futureNonceRetention;

        public TooFarNonceFilter(ITxPoolConfig txPoolConfig, IAccountStateProvider accountStateProvider, TxDistinctSortedPool txs, ILogger logger)
        {
            _futureNonceRetention  = txPoolConfig.FutureNonceRetention;
            _txs = txs;
            _accounts = accountStateProvider;
            _logger = logger;
        }
            
        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            int numberOfSenderTxsInPending = _txs.GetBucketCount(tx.SenderAddress);
            bool isTxPoolFull = _txs.IsFull();
            UInt256 currentNonce = _accounts.GetAccount(tx.SenderAddress!).Nonce;
            bool isTxNonceNextInOrder = tx.Nonce <= (long)currentNonce + numberOfSenderTxsInPending;
            bool isTxNonceTooFarInFuture = tx.Nonce > currentNonce + _futureNonceRetention;
            if (isTxPoolFull && !isTxNonceNextInOrder || isTxNonceTooFarInFuture)
            {
                Metrics.PendingTransactionsTooFarInFuture++;
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce in future.");
                return (false, AddTxResult.NonceTooFarInTheFuture);
            }

            return (true, null);
        }
    }
}
