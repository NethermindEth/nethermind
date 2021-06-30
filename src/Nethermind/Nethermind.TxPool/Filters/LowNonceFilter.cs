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

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions where nonce is lower than the current sender account nonce.
    /// </summary>
    internal class LowNonceFilter : IIncomingTxFilter
    {
        private readonly IAccountStateProvider _accounts;
        private readonly ILogger _logger;

        public LowNonceFilter(IAccountStateProvider accountStateProvider, ILogger logger)
        {
            _accounts = accountStateProvider;
            _logger = logger;
        }
            
        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            // As we have limited number of transaction that we store in mem pool its fairly easy to fill it up with
            // high-priority garbage transactions. We need to filter them as much as possible to use the tx pool space
            // efficiently. One call to get account from state is not that costly and it only happens after previous checks.
            // This was modeled by OpenEthereum behavior.
            Account account = _accounts.GetAccount(tx.SenderAddress!);
            UInt256 currentNonce = account.Nonce;
            if (tx.Nonce < currentNonce)
            {
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                return (false, AddTxResult.OldNonce);
            }

            return (true, null);
        }
    }
}
