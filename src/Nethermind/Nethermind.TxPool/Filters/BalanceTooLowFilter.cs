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
        private readonly TxDistinctSortedPool _txs = txs;
        private readonly TxDistinctSortedPool _blobTxs = blobTxs;
        private readonly ILogger _logger = logger;

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            // Frame transactions are deferred to FrameTxPayerExposureFilter, which runs after payer resolution.
            if (tx.IsFree() || tx.SupportsFrames)
            {
                return AcceptTxResult.Accepted;
            }

            AccountStruct account = state.SenderAccount;
            UInt256 balance = account.Balance;

            TxDistinctSortedPool pool = tx.CarriesBlobs ? _blobTxs : _txs;
            bool overflow = tx.IsOverflowWhenSummingSenderBucket(pool, account.Nonce, unreservedOnly: false, state.HeadSpec.IsEip8250Enabled, out UInt256 cumulativeCost);
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
