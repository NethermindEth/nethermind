// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions which gas payments overflow uint256 or simply exceed sender balance
    /// </summary>
    internal sealed class BalanceZeroFilter : IIncomingTxFilter
    {
        private readonly ILogger _logger;

        public BalanceZeroFilter(ILogger logger)
        {
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            Account account = state.SenderAccount;
            UInt256 balance = account.Balance;

            if (balance.IsZero)
            {
                Metrics.PendingTransactionsZeroBalance++;
                return AcceptTxResult.InsufficientFunds.WithMessage("Account balance: 0");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
