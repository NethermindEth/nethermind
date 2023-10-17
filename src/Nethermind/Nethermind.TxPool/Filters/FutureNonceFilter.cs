// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool.Filters;

public class FutureNonceFilter : IIncomingTxFilter
{
    private readonly ITxPoolConfig _txPoolConfig;

    public FutureNonceFilter(ITxPoolConfig txPoolConfig)
    {
        _txPoolConfig = txPoolConfig;
    }

    public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        int relevantMaxPendingTxsPerSender = (tx.SupportsBlobs
            ? _txPoolConfig.MaxPendingBlobTxsPerSender
            : _txPoolConfig.MaxPendingTxsPerSender);

        // MaxPendingTxsPerSender/MaxPendingBlobTxsPerSender equal 0 means no limit
        if (relevantMaxPendingTxsPerSender == 0)
        {
            return AcceptTxResult.Accepted;
        }

        UInt256 currentNonce = state.SenderAccount.Nonce;
        bool overflow = UInt256.AddOverflow(currentNonce, (UInt256)relevantMaxPendingTxsPerSender, out UInt256 maxAcceptedNonce);

        // Overflow means that gap between current nonce of sender and UInt256.MaxValue is lower than allowed number
        // of pending transactions. As lower nonces were rejected earlier, here it means tx accepted.
        // So we are rejecting tx only if there is no overflow.
        if (tx.Nonce > maxAcceptedNonce && !overflow)
        {
            Metrics.PendingTransactionsNonceTooFarInFuture++;
            return AcceptTxResult.NonceTooFarInFuture;
        }

        return AcceptTxResult.Accepted;
    }
}
