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
        private readonly bool _thereIsPriorityContract;
        private readonly ILogger _logger;

        public BalanceZeroFilter(bool thereIsPriorityContract, ILogger logger)
        {
            _thereIsPriorityContract = thereIsPriorityContract;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            AccountStruct account = state.SenderAccount;
            UInt256 balance = account.Balance;

            bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
            if (!_thereIsPriorityContract && !tx.IsFree() && balance.IsZero)
            {
                Metrics.PendingTransactionsZeroBalance++;
                return isNotLocal ?
                    AcceptTxResult.InsufficientFunds :
                    AcceptTxResult.InsufficientFunds.WithMessage("Balance is zero, cannot pay gas");
            }

            if (balance < tx.Value)
            {
                Metrics.PendingTransactionsBalanceBelowValue++;
                return isNotLocal ?
                    AcceptTxResult.InsufficientFunds :
                    AcceptTxResult.InsufficientFunds.WithMessage($"Balance is {balance} less than sending value {tx.Value}");
            }

            if (tx.IsOverflowInTxCostAndValue(out UInt256 txCostAndValue))
            {
                Metrics.PendingTransactionsBalanceBelowValue++;
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, cost overflow.");
                return AcceptTxResult.Int256Overflow;
            }

            if (balance < txCostAndValue)
            {
                Metrics.PendingTransactionsBalanceBelowValue++;
                return isNotLocal ?
                    AcceptTxResult.InsufficientFunds :
                    AcceptTxResult.InsufficientFunds.WithMessage($"Balance is {balance} less than sending value + gas {txCostAndValue}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
