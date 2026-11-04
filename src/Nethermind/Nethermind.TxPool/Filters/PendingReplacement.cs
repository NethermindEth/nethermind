// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters;

/// <summary>Finds the pending transaction an incoming one would displace.</summary>
/// <remarks>Shared by the EIP-8141 admission gates that discount a replacement: they bound different
/// quantities but must agree on which pending entry is being replaced, and the lookup encodes a pool
/// invariant (one competing entry per nonce key) that two copies would drift on.</remarks>
internal static class PendingReplacement
{
    /// <summary>The pending transaction <paramref name="tx"/> would displace, or <c>null</c> when it joins
    /// the pending set instead.</summary>
    /// <remarks>Matched on the pool's own competing key, so an EIP-8250 same-nonce transaction in another
    /// nonce-key domain is not a replacement.</remarks>
    public static Transaction? Find(Transaction tx, TxDistinctSortedPool standardPool, TxDistinctSortedPool blobPool) =>
        (tx.CarriesBlobs ? blobPool : standardPool).FindPendingReplacement(tx);
}
