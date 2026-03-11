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
        private readonly ITxPoolCostAndFundsProvider _costAndFundsProvider;
        private readonly ILogger _logger;

        public BalanceTooLowFilter(
            TxDistinctSortedPool txs,
            TxDistinctSortedPool blobTxs,
            ILogger logger,
            ITxPoolCostAndFundsProvider? costAndFundsProvider = null)
        {
            _txs = txs;
            _blobTxs = blobTxs;
            _costAndFundsProvider = costAndFundsProvider ?? DefaultTxPoolCostAndFundsProvider.Instance;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (tx.IsFree())
            {
                return AcceptTxResult.Accepted;
            }

            AccountStruct account = state.SenderAccount;
            UInt256 additionalFunds = _costAndFundsProvider.GetAdditionalFunds(tx);
            if (UInt256.AddOverflow(account.Balance, additionalFunds, out UInt256 balance))
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, cost overflow.");
                return AcceptTxResult.Int256Overflow;
            }

            UInt256 cumulativeCost = UInt256.Zero;
            bool overflow = false;
            Transaction[] sameTypeTxs = tx.SupportsBlobs
                ? _blobTxs.GetBucketSnapshot(tx.SenderAddress!) // it will create a snapshot of light txs (without actual blobs)
                : _txs.GetBucketSnapshot(tx.SenderAddress!);
            // tx.SenderAddress! as unknownSenderFilter will run before this one

            for (int i = 0; i < sameTypeTxs.Length; i++)
            {
                Transaction otherTx = sameTypeTxs[i];
                if (otherTx.Nonce < account.Nonce)
                {
                    continue;
                }

                if (otherTx.Nonce < tx.Nonce)
                {
                    overflow |= !_costAndFundsProvider.TryGetTransactionCost(otherTx, out UInt256 otherTxCost)
                        || UInt256.AddOverflow(cumulativeCost, otherTxCost, out cumulativeCost);
                }
                else
                {
                    break;
                }
            }

            overflow |= !_costAndFundsProvider.TryGetTransactionCost(tx, out UInt256 txCost)
                || UInt256.AddOverflow(cumulativeCost, txCost, out cumulativeCost);

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
