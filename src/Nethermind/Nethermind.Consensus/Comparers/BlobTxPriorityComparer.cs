// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Comparers;

/// <summary>
/// Tie-breaker comparer that prefers blob transactions over non-blob transactions.
/// Returns <see cref="TxComparisonResult.FirstIsBetter"/> if only x supports blobs,
/// <see cref="TxComparisonResult.SecondIsBetter"/> if only y supports blobs,
/// or <see cref="TxComparisonResult.Equal"/> if both or neither support blobs.
/// </summary>
public sealed class BlobTxPriorityComparer : IComparer<Transaction>
{
    public static readonly BlobTxPriorityComparer Instance = new();

    private BlobTxPriorityComparer() { }

    public int Compare(Transaction? x, Transaction? y)
    {
        if (ReferenceEquals(x, y)) return TxComparisonResult.Equal;
        if (x is null) return TxComparisonResult.FirstIsBetter;
        if (y is null) return TxComparisonResult.SecondIsBetter;

        return x.SupportsBlobs == y.SupportsBlobs ? TxComparisonResult.Equal :
            x.SupportsBlobs ? TxComparisonResult.FirstIsBetter : TxComparisonResult.SecondIsBetter;
    }
}
