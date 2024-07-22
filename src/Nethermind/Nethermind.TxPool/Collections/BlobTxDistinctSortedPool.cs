// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
    : TxDistinctSortedPool(capacity, comparer, logManager)
{
    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    protected override string ShortPoolName => "BlobPool";

    /// <summary>
    /// For tests only - to test sorting
    /// </summary>
    internal void TryGetBlobTxSortingEquivalent(Hash256 hash, out Transaction? lightBlobTx)
        => base.TryGetValue(hash, out lightBlobTx);
}
