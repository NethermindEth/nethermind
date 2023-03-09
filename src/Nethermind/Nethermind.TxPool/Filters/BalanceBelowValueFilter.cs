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
    internal sealed class BalanceBelowValueFilter : IIncomingTxFilter
    {
        private readonly ILogger _logger;

        public BalanceBelowValueFilter(ILogger logger)
        {
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            Account account = state.SenderAccount;
            UInt256 balance = account.Balance;

            bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
            if (balance < tx.Value)
            {
                Metrics.PendingTransactionsBalanceBelowValue++;
                return isNotLocal ?
                    AcceptTxResult.InsufficientFunds :
                    AcceptTxResult.InsufficientFunds.WithMessage($"Balance is {balance} - less than sending value {tx.Value}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
