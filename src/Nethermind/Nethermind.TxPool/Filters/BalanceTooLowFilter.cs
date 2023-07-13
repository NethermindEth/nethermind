// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        private readonly TxDistinctSortedPool _blobTxs;
        private readonly ILogger _logger;

        public BalanceTooLowFilter(TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs, ILogger logger)
        {
            _txs = txs;
            _blobTxs = blobTxs;
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
            Transaction[] sameTypeTxs = tx.SupportsBlobs
                ? _blobTxs.GetBucketSnapshot(tx.SenderAddress!)
                : _txs.GetBucketSnapshot(tx.SenderAddress!); // since unknownSenderFilter will run before this one

            for (int i = 0; i < sameTypeTxs.Length; i++)
            {
                Transaction otherTx = sameTypeTxs[i];
                if (otherTx.Nonce < account.Nonce)
                {
                    continue;
                }

                if (otherTx.Nonce < tx.Nonce)
                {
                    overflow |= otherTx.IsOverflowWhenAddingTxCostToCumulative(cumulativeCost, out cumulativeCost);
                }
                else
                {
                    break;
                }
            }

            overflow |= tx.IsOverflowWhenAddingTxCostToCumulative(cumulativeCost, out cumulativeCost);

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
