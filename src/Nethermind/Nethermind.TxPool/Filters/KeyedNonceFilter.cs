// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>Admits an EIP-8250 transaction selecting protocol-managed nonce domains, replacing the account-nonce checks that do not apply to it.</summary>
/// <remarks><c>nonce_seq</c> must equal every selected key's current sequence, so a keyed transaction is never future or gapped and never <see cref="AcceptTxResult.OldNonce"/>.</remarks>
internal sealed class KeyedNonceFilter(
    IReadOnlyStateProvider stateProvider,
    ITxPoolConfig txPoolConfig,
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (tx.NonceKeys is not { } nonceKeys || !KeyedNonceManager.UsesKeyedDomain(nonceKeys))
        {
            return AcceptTxResult.Accepted;
        }

        if (!KeyedNonceManager.IsNonceSetValid(stateProvider, tx.SenderAddress!, nonceKeys, tx.Nonce))
        {
            Metrics.PendingTransactionsKeyedNonceUnmet++;
            return AcceptTxResult.KeyedNonceUnmet;
        }

        if (ExceedsPerSenderLimit(tx))
        {
            Metrics.PendingTransactionsNonceTooFarInFuture++;
            return AcceptTxResult.NonceTooFarInFuture.WithMessage("too many pending keyed nonce transactions for sender");
        }

        return AcceptTxResult.Accepted;
    }

    /// <summary>Whether <paramref name="tx"/> would take its sender past the configured pending limit.</summary>
    /// <remarks>The account-nonce filters bound a sender by nonce distance, which keyed domains are immune to:
    /// every fresh key is current at sequence zero, so the same configured limit has to hold as a pending count.
    /// A transaction displacing a pending one adds nothing to that count. The default of no limit is the
    /// account-nonce default too, and leaves a keyed sender where an ordinary one is: bounded by the pool's
    /// fee-ordered eviction rather than per sender.</remarks>
    private bool ExceedsPerSenderLimit(Transaction tx)
    {
        // A limit of 0 means no limit.
        int limit = tx.CarriesBlobs ? txPoolConfig.MaxPendingBlobTxsPerSender : txPoolConfig.MaxPendingTxsPerSender;
        return limit > 0
               && (tx.CarriesBlobs ? blobPool : standardPool).GetBucketCount(tx.SenderAddress!) >= limit
               && PendingReplacement.Find(tx, standardPool, blobPool) is null;
    }
}
