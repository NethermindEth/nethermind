// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Rejects an EIP-8250 keyed-nonce frame transaction whose nonce-key set intersects a pending transaction of
/// the same sender, so that no two pending transactions of one sender share a <c>(sender, nonce_key)</c>.
/// </summary>
/// <remarks>
/// EIP-8250 admits several pending frame transactions from one sender only on disjoint non-zero key sets, and
/// MATCHA is that policy. An overlap, not only an equal set, correlates invalidation: one on-chain change to a
/// shared key invalidates every pending transaction naming it. A transaction that replaces the same competing
/// slot is not an overlap and is left to the replacement rules.
/// </remarks>
internal sealed class KeyedNonceDisjointnessFilter(
    TxDistinctSortedPool standardPool,
    TxDistinctSortedPool blobPool) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (!tx.SupportsFrames || !KeyedNonceManager.UsesKeyedNonce(tx))
        {
            return AcceptTxResult.Accepted;
        }

        OverlapSearch search = new(tx);
        standardPool.VisitBucket(tx.SenderAddress!, ref search, Visit);
        if (!search.Overlaps)
        {
            blobPool.VisitBucket(tx.SenderAddress!, ref search, Visit);
        }

        if (search.Overlaps)
        {
            Interlocked.Increment(ref Metrics.PendingTransactionsKeyedNonceOverlap);
            return AcceptTxResult.KeyedNonceOverlap;
        }

        return AcceptTxResult.Accepted;
    }

    private static bool Visit(Transaction pending, ref OverlapSearch state)
    {
        if (CompetingTransactionEqualityComparer.Instance.Equals(state.Tx, pending))
        {
            return true;
        }

        if (pending.NonceKeys is { } pendingKeys && Intersects(state.Keys, pendingKeys))
        {
            state.Overlaps = true;
            return false;
        }

        return true;
    }

    private static bool Intersects(ReadOnlySpan<UInt256> incoming, ReadOnlySpan<UInt256> pending)
    {
        foreach (UInt256 key in incoming)
        {
            if (key.IsZero)
            {
                continue;
            }

            foreach (UInt256 other in pending)
            {
                if (key == other)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private struct OverlapSearch(Transaction tx)
    {
        public readonly Transaction Tx = tx;
        public readonly UInt256[] Keys = tx.NonceKeys ?? [];
        public bool Overlaps;
    }
}
