// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        HashSet<string> excluded = new(excludedProperties)
        {
            nameof(Transaction.MaxPriorityFeePerGas),
            nameof(Transaction.ValueRef)
        };

        using (Assert.EnterMultipleScope())
        {
            foreach (PropertyInfo property in typeof(Transaction).GetProperties())
            {
                if (excluded.Contains(property.Name) ||
                    property.GetIndexParameters().Length != 0 ||
                    property.PropertyType.IsByRef)
                {
                    continue;
                }

                object actualValue = property.GetValue(actual);
                object expectedValue = property.GetValue(expected);

                AssertValuesEquivalent(actualValue, expectedValue, property.Name);
            }
        }
    }

    private static void AssertValuesEquivalent(object actualValue, object expectedValue, string propertyName)
    {
        if (actualValue is ReadOnlyMemory<byte> actualMemory && expectedValue is ReadOnlyMemory<byte> expectedMemory)
        {
            Assert.That(actualMemory.ToArray(), Is.EqualTo(expectedMemory.ToArray()), propertyName);
        }
        else if (actualValue is byte[] actualBytes && expectedValue is byte[] expectedBytes)
        {
            Assert.That(actualBytes, Is.EqualTo(expectedBytes), propertyName);
        }
        else if (actualValue is byte[][] actualByteArrays && expectedValue is byte[][] expectedByteArrays)
        {
            AssertByteArraysEquivalent(actualByteArrays, expectedByteArrays, propertyName);
        }
        else if (actualValue is ShardBlobNetworkWrapper actualWrapper && expectedValue is ShardBlobNetworkWrapper expectedWrapper)
        {
            AssertShardBlobNetworkWrapperEquivalent(actualWrapper, expectedWrapper, propertyName);
        }
        else
        {
            Assert.That(actualValue, Is.EqualTo(expectedValue), propertyName);
        }
    }

    private static void AssertShardBlobNetworkWrapperEquivalent(
        ShardBlobNetworkWrapper actual,
        ShardBlobNetworkWrapper expected,
        string propertyName)
        => Assert.Multiple(() =>
        {
            Assert.That(actual.Version, Is.EqualTo(expected.Version), $"{propertyName}.{nameof(ShardBlobNetworkWrapper.Version)}");
            AssertByteArraysEquivalent(actual.Blobs, expected.Blobs, $"{propertyName}.{nameof(ShardBlobNetworkWrapper.Blobs)}");
            AssertByteArraysEquivalent(actual.Commitments, expected.Commitments, $"{propertyName}.{nameof(ShardBlobNetworkWrapper.Commitments)}");
            AssertByteArraysEquivalent(actual.Proofs, expected.Proofs, $"{propertyName}.{nameof(ShardBlobNetworkWrapper.Proofs)}");
        });

    private static void AssertByteArraysEquivalent(byte[][] actual, byte[][] expected, string propertyName)
        => Assert.That(
            actual.Select(static value => value?.ToHexString()).ToArray(),
            Is.EqualTo(expected.Select(static value => value?.ToHexString()).ToArray()),
            propertyName);
}
