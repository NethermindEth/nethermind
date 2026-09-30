// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Builders;

public static class TransactionPoolTestHelper
{
    /// <summary>Refills the transaction pool and captures the identities retained for ownership assertions.</summary>
    public static HashSet<Transaction> Refill()
    {
        HashSet<Transaction> pooled = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < 2_048; i++) pooled.Add(TxDecoder.TxObjectPool.Get());
        foreach (Transaction transaction in pooled) TxDecoder.TxObjectPool.Return(transaction);
        return pooled;
    }
}
