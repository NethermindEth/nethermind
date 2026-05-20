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
    internal sealed class BalanceTooLowFilter(TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs, ILogger logger) : IIncomingTxFilter
    {
        private struct BucketBalanceState(UInt256 accountNonce, UInt256 txNonce)
        {
            public readonly UInt256 AccountNonce = accountNonce;
            public readonly UInt256 TxNonce = txNonce;
            public UInt256 CumulativeCost = UInt256.Zero;
            public bool Overflow = false;
        }

        private readonly TxDistinctSortedPool _txs = txs;
        private readonly TxDistinctSortedPool _blobTxs = blobTxs;
        private readonly ILogger _logger = logger;

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (tx.IsFree())
            {
                return AcceptTxResult.Accepted;
            }

            AccountStruct account = state.SenderAccount;
            UInt256 balance = account.Balance;

            BucketBalanceState bucketBalanceState = new(account.Nonce, tx.Nonce);
            TxDistinctSortedPool pool = tx.SupportsBlobs ? _blobTxs : _txs;
            // tx.SenderAddress! as unknownSenderFilter will run before this one
            pool.VisitBucket(tx.SenderAddress!, ref bucketBalanceState, static (Transaction otherTx, ref BucketBalanceState bucketState) =>
            {
                if (otherTx.Nonce < bucketState.AccountNonce)
                {
                    return true;
                }

                if (otherTx.Nonce >= bucketState.TxNonce)
                {
                    return false;
                }

                bucketState.Overflow |= otherTx.IsOverflowWhenAddingTxCostToCumulative(bucketState.CumulativeCost, out bucketState.CumulativeCost);
                return true;
            });

            bool overflow = bucketBalanceState.Overflow;
            overflow |= tx.IsOverflowWhenAddingTxCostToCumulative(bucketBalanceState.CumulativeCost, out bucketBalanceState.CumulativeCost);

            if (overflow)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, cost overflow.");
                return AcceptTxResult.Int256Overflow;
            }

            if (balance < bucketBalanceState.CumulativeCost)
            {
                Metrics.PendingTransactionsTooLowBalance++;

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, insufficient funds.");
                }

                bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
                return isNotLocal ?
                    AcceptTxResult.InsufficientFunds :
                    AcceptTxResult.InsufficientFunds.WithMessage($"Account balance: {balance}, cumulative cost: {bucketBalanceState.CumulativeCost}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
