// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>Rejects a transaction that competes with a pending one but does not pay enough to replace it.</summary>
/// <remarks>The pool makes the same decision on insert; deciding it here first spares the signature recoveries and
/// blob proof checks the later filters perform, which a same-nonce transaction that cannot be admitted would
/// otherwise pay for. Other transactions face no such work and are left to the insert, sparing them the bucket lookup.</remarks>
internal sealed class UnderpricedReplacementFilter(TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if ((!tx.HasAuthorizationList && !tx.CarriesBlobs && !tx.SupportsFrames)
            || PendingReplacement.Find(tx, standardPool, blobPool) is not { } replaced
            || (tx.CarriesBlobs ? blobPool : standardPool).CanReplace(tx, replaced))
        {
            return AcceptTxResult.Accepted;
        }

        Metrics.PendingTransactionsPassedFiltersButCannotReplace++;
        return AcceptTxResult.ReplacementNotAllowed;
    }
}
