// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions which gas payments overflow uint256 or simply exceed sender balance
    /// </summary>
    internal sealed class BalanceTooLowFilter : IIncomingTxFilter
    {
        private readonly TxDistinctSortedPool _txs;
        private readonly ILogger _logger;

        public BalanceTooLowFilter(TxDistinctSortedPool txs, ILogger logger)
        {
            _txs = txs;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (tx.IsFree())
            {
                return AcceptTxResult.Accepted;
            }

            Account account = state.SenderAccount;
            UInt256 balance = account.Balance;

            UInt256 cumulativeCost = UInt256.Zero;
            bool overflow = false;
            Transaction[] transactions = _txs.GetBucketSnapshot(tx.SenderAddress!); // since unknownSenderFilter will run before this one

            for (int i = 0; i < transactions.Length; i++)
            {
                Transaction otherTx = transactions[i];
                if (otherTx.Nonce < account.Nonce)
                {
                    continue;
                }

                if (otherTx.Nonce < tx.Nonce)
                {
                    overflow |= UInt256.MultiplyOverflow(otherTx.MaxFeePerGas, (UInt256)otherTx.GasLimit, out UInt256 maxTxCost);
                    overflow |= UInt256.AddOverflow(cumulativeCost, maxTxCost, out cumulativeCost);
                    overflow |= UInt256.AddOverflow(cumulativeCost, otherTx.Value, out cumulativeCost);
                }
                else
                {
                    break;
                }
            }

            overflow |= UInt256.MultiplyOverflow(tx.MaxFeePerGas, (UInt256)tx.GasLimit, out UInt256 cost);
            overflow |= UInt256.AddOverflow(cost, tx.Value, out cost);
            overflow |= UInt256.AddOverflow(cost, cumulativeCost, out cumulativeCost);
            if (overflow)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, cost overflow.");
                return AcceptTxResult.Int256Overflow;
            }

            if (balance < cumulativeCost)
            {
                Metrics.PendingTransactionsTooLowBalance++;

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, insufficient funds.");
                }

                bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
                return isNotLocal ?
                    AcceptTxResult.InsufficientFunds :
                    AcceptTxResult.InsufficientFunds.WithMessage($"Account balance: {balance}, cumulative cost: {cumulativeCost}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
