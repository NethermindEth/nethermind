// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Filters;

/// <summary>Finds the pending transaction an incoming one would displace.</summary>
/// <remarks>Shared by the EIP-8141 admission gates that discount a replacement: they bound different
/// quantities but must agree on which pending entry is being replaced, and the walk encodes a pool
/// invariant (one competing entry per nonce key) that two copies would drift on.</remarks>
internal static class PendingReplacement
{
    /// <summary>The pending transaction <paramref name="tx"/> would displace, or <c>null</c> when it joins
    /// the pending set instead.</summary>
    /// <remarks>Matched on the pool's own competing key, so an EIP-8250 same-nonce transaction in another
    /// nonce-key domain is not a replacement.</remarks>
    public static Transaction? Find(Transaction tx, TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool)
    {
        ReplacementSearch search = new(tx);
        TxDistinctSortedPool pool = tx.CarriesBlobs ? blobPool : standardPool;
        pool.VisitBucket(tx.SenderAddress!, ref search, static (Transaction pending, ref ReplacementSearch state) =>
        {
            // Buckets are visited in ascending nonce order, so skip below and stop past the replaced nonce.
            if (pending.Nonce < state.Nonce) return true;
            if (pending.Nonce > state.Nonce) return false;

            // Same nonce, another domain: only one entry can compete, so keep looking for it.
            if (!CompetingTransactionEqualityComparer.Instance.Equals(state.Tx, pending)) return true;

            state.Replaced = pending;
            return false;
        });

        return search.Replaced;
    }

    private struct ReplacementSearch(Transaction tx)
    {
        public readonly Transaction Tx = tx;
        public readonly ulong Nonce = tx.Nonce;
        public Transaction? Replaced;
    }
}
