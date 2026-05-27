// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.TxPool;
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
        params string[] excludedProperties)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected));
            return;
        }

        Transaction actualComparable = ToComparableTransaction(actual);
        Transaction expectedComparable = ToComparableTransaction(expected);
        ApplyExclusions(actualComparable, expectedComparable, excludedProperties);

        Assert.Multiple(() =>
        {
            AssertPreservedPoolFields(actual, expected, excludedProperties);
            Assert.That(actualComparable, Is.EqualTo(expectedComparable));
        });
    }

    private static Transaction ToComparableTransaction(Transaction transaction)
    {
        Transaction comparable = new();
        transaction.CopyTo(comparable);
        comparable.Hash = transaction.Hash;
        return comparable;
    }

    private static void ApplyExclusions(Transaction actual, Transaction expected, string[] excludedProperties)
    {
        for (int i = 0; i < excludedProperties.Length; i++)
        {
            switch (excludedProperties[i])
            {
                case nameof(Transaction.ChainId):
                    actual.ChainId = expected.ChainId;
                    break;
                case nameof(Transaction.Data):
                    actual.Data = expected.Data;
                    break;
                case nameof(Transaction.MaxFeePerGas):
                case nameof(Transaction.DecodedMaxFeePerGas):
                    actual.DecodedMaxFeePerGas = expected.DecodedMaxFeePerGas;
                    break;
            }
        }
    }

    private static void AssertPreservedPoolFields(Transaction actual, Transaction expected, string[] excludedProperties)
    {
        if (!IsExcluded(excludedProperties, nameof(Transaction.SenderAddress)))
        {
            Assert.That(actual.SenderAddress, Is.EqualTo(expected.SenderAddress));
        }

        if (!IsExcluded(excludedProperties, nameof(Transaction.Timestamp)))
        {
            Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp));
        }

        if (!IsExcluded(excludedProperties, nameof(Transaction.GasBottleneck)))
        {
            Assert.That(actual.GasBottleneck, Is.EqualTo(expected.GasBottleneck));
        }

        if (!IsExcluded(excludedProperties, nameof(Transaction.SpentGas)))
        {
            Assert.That(actual.SpentGas, Is.EqualTo(expected.SpentGas));
        }

        if (!IsExcluded(excludedProperties, nameof(Transaction.BlockGasUsed)))
        {
            Assert.That(actual.BlockGasUsed, Is.EqualTo(expected.BlockGasUsed));
        }

        if (!IsExcluded(excludedProperties, nameof(Transaction.PoolIndex)))
        {
            Assert.That(actual.PoolIndex, Is.EqualTo(expected.PoolIndex));
        }

        if (!IsExcluded(excludedProperties, nameof(LightTransaction.ProofVersion)))
        {
            Assert.That(actual.GetProofVersion(), Is.EqualTo(expected.GetProofVersion()));
        }
    }

    private static bool IsExcluded(string[] excludedProperties, string propertyName) => excludedProperties.Contains(propertyName);
}
