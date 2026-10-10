// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Builders;

public static class TransactionPoolTestHelper
{
    /// <summary>Refills the transaction pool and captures the identities retained for ownership assertions.</summary>
    public static HashSet<Transaction> Refill()
    {
        HashSet<Transaction> pooled = [with(ReferenceEqualityComparer.Instance)];
        for (int i = 0; i < 2_048; i++) pooled.Add(TxDecoder.TxObjectPool.Get());
        foreach (Transaction transaction in pooled) TxDecoder.TxObjectPool.Return(transaction);
        return pooled;
    }

    /// <summary>Asserts that every retained transaction remains available, then returns the probe rentals.</summary>
    public static void AssertAllReturned(HashSet<Transaction> pooled)
    {
        List<Transaction> rented = [];
        try
        {
            for (int i = 0; i < pooled.Count; i++) rented.Add(TxDecoder.TxObjectPool.Get());
            Assert.That(rented, Is.SubsetOf(pooled));
        }
        finally
        {
            foreach (Transaction transaction in rented) TxDecoder.TxObjectPool.Return(transaction);
        }
    }
}
