// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

internal static class TransactionAssertions
{
    public static void AssertEquivalent(
        IEnumerable<Transaction> actual,
        IEnumerable<Transaction> expected,
        params string[] excludedProperties)
    {
        Transaction[] actualArray = actual.ToArray();
        Transaction[] expectedArray = expected.ToArray();

        Assert.That(actualArray.Length, Is.EqualTo(expectedArray.Length));

        if (actualArray.All(static tx => tx.Hash is not null) && expectedArray.All(static tx => tx.Hash is not null))
        {
            foreach (Transaction expectedTx in expectedArray)
            {
                Transaction actualTx = actualArray.Single(tx => tx.Hash == expectedTx.Hash);
                AssertEquivalent(actualTx, expectedTx, excludedProperties);
            }
        }
        else
        {
            for (int i = 0; i < actualArray.Length; i++)
            {
                AssertEquivalent(actualArray[i], expectedArray[i], excludedProperties);
            }
        }
    }

    public static void AssertEquivalent(
        Transaction actual,
        Transaction expected,
        params string[] excludedProperties) =>
        Assert.That(actual, Is.EqualTo(expected).UsingTransactionComparer(excludedProperties));
}
