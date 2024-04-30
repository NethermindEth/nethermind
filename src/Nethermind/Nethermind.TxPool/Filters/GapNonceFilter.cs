// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions with nonces set too far in the future.
    /// Without this filter it would be possible to fill in TX pool with transactions that have low chance of being executed soon.
    /// </summary>
    internal sealed class GapNonceFilter : IIncomingTxFilter
    {
        private readonly TxDistinctSortedPool _txs;
        private readonly TxDistinctSortedPool _blobTxs;
        private readonly ILogger _logger;

        public GapNonceFilter(TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs, ILogger logger)
        {
            _txs = txs;
            _blobTxs = blobTxs;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            bool isLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
            bool nonceGapsAllowed = isLocal || !_txs.IsFull();
            if (nonceGapsAllowed && !tx.SupportsBlobs)
            {
                return AcceptTxResult.Accepted;
            }

            int numberOfSenderTxsInPending = tx.SupportsBlobs
                ? _blobTxs.GetBucketCount(tx.SenderAddress!)
                : _txs.GetBucketCount(tx.SenderAddress!); // since unknownSenderFilter will run before this one
            ulong currentNonce = (ulong)state.SenderAccount.Nonce; // TODO: This cast could break once nonces exceed ulong.MaxValue
            ulong nextNonceInOrder = currentNonce + (ulong)numberOfSenderTxsInPending;
            bool isTxNonceNextInOrder = tx.Nonce <= nextNonceInOrder;
            if (!isTxNonceNextInOrder)
            {
                Metrics.PendingTransactionsNonceGap++;
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce in future.");
                }

                return !isLocal ?
                    AcceptTxResult.NonceGap :
                    AcceptTxResult.NonceGap.WithMessage(TxErrorMessages.FutureNonce(in nextNonceInOrder, tx.Nonce));
            }

            return AcceptTxResult.Accepted;
        }
    }
}
