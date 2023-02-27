// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions which gas payments overflow uint256 or simply exceed sender balance
    /// </summary>
    internal class TooExpensiveTxFilter : IIncomingTxFilter
    {
        private readonly TxDistinctSortedPool _txs;
        private readonly ILogger _logger;

        public TooExpensiveTxFilter(TxDistinctSortedPool txs, ILogger logger)
        {
            _txs = txs;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            Account account = state.SenderAccount;
            UInt256 balance = account.Balance;
            UInt256 cumulativeCost = UInt256.Zero;
            bool overflow = false;
            Transaction[] transactions = _txs.GetBucketSnapshot(tx.SenderAddress!); // since unknownSenderFilter will run before this one

            for (int i = 0; i < transactions.Length; i++)
            {
                if (transactions[i].Nonce < account.Nonce)
                {
                    continue;
                }

                if (transactions[i].Nonce < tx.Nonce)
                {
                    overflow |= UInt256.MultiplyOverflow(transactions[i].MaxFeePerGas, (UInt256)transactions[i].GasLimit, out UInt256 maxTxCost);
                    overflow |= UInt256.AddOverflow(cumulativeCost, maxTxCost, out cumulativeCost);
                    overflow |= UInt256.AddOverflow(cumulativeCost, transactions[i].Value, out cumulativeCost);
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
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, insufficient funds.");
                return AcceptTxResult.InsufficientFunds.WithMessage($"Account balance: {balance}, cumulative cost: {cumulativeCost}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
