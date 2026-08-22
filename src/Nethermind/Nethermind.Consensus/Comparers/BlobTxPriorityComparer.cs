// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Comparers;

/// <summary>
/// Tie-breaker comparer that prefers blob transactions over non-blob transactions
/// and, between blob transactions, prefers the higher blob fee cap.
/// </summary>
public sealed class BlobTxPriorityComparer : IComparer<Transaction>
{
    public static readonly BlobTxPriorityComparer Instance = new();

    private BlobTxPriorityComparer() { }

    public int Compare(Transaction? x, Transaction? y)
    {
        if (ReferenceEquals(x, y)) return TxComparisonResult.Equal;
        if (x is null) return TxComparisonResult.XFirst;
        if (y is null) return TxComparisonResult.YFirst;

        if (x.SupportsBlobs != y.SupportsBlobs)
            return x.SupportsBlobs ? TxComparisonResult.XFirst : TxComparisonResult.YFirst;

        if (!x.SupportsBlobs)
            return TxComparisonResult.Equal;

        return Nullable.Compare(y.MaxFeePerBlobGas, x.MaxFeePerBlobGas);
    }
}
