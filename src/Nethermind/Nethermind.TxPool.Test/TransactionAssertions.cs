// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        params string[] excludedProperties)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected));
            return;
        }

        if (actual is LightTransaction || expected is LightTransaction)
        {
            AssertLightTransactionEquivalent(actual, expected, excludedProperties);
            return;
        }

        Assert.That(actual, Is.EqualTo(expected).UsingTransactionComparer(excludedProperties));
    }

    private static void AssertLightTransactionEquivalent(Transaction actual, Transaction expected, string[] excludedProperties)
    {
        Assert.That(actual.Type, Is.EqualTo(expected.Type), nameof(Transaction.Type));
        Assert.That(actual.Hash, Is.EqualTo(expected.Hash), nameof(Transaction.Hash));
        Assert.That(actual.Nonce, Is.EqualTo(expected.Nonce), nameof(Transaction.Nonce));
        Assert.That(actual.Value, Is.EqualTo(expected.Value), nameof(Transaction.Value));
        Assert.That(actual.GasLimit, Is.EqualTo(expected.GasLimit), nameof(Transaction.GasLimit));
        Assert.That(actual.GasPrice, Is.EqualTo(expected.GasPrice), nameof(Transaction.GasPrice));
        Assert.That(actual.DecodedMaxFeePerGas, Is.EqualTo(expected.DecodedMaxFeePerGas), nameof(Transaction.DecodedMaxFeePerGas));
        Assert.That(actual.MaxFeePerBlobGas, Is.EqualTo(expected.MaxFeePerBlobGas), nameof(Transaction.MaxFeePerBlobGas));
        Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp), nameof(Transaction.Timestamp));
        Assert.That(actual.GetLength(), Is.EqualTo(expected.GetLength()), nameof(Transaction.GetLength));
        Assert.That(actual.GetProofVersion(), Is.EqualTo(expected.GetProofVersion()), nameof(Transaction.GetProofVersion));
        AssertByteArraysEquivalent(actual.BlobVersionedHashes, expected.BlobVersionedHashes);

        if (!IsExcluded(excludedProperties, nameof(Transaction.SenderAddress)))
        {
            Assert.That(actual.SenderAddress, Is.EqualTo(expected.SenderAddress), nameof(Transaction.SenderAddress));
        }

        if (!IsExcluded(excludedProperties, nameof(Transaction.PoolIndex)))
        {
            Assert.That(actual.PoolIndex, Is.EqualTo(expected.PoolIndex), nameof(Transaction.PoolIndex));
        }

        if (!IsExcluded(excludedProperties, nameof(Transaction.GasBottleneck)))
        {
            Assert.That(actual.GasBottleneck, Is.EqualTo(expected.GasBottleneck), nameof(Transaction.GasBottleneck));
        }

        if (actual is LightTransaction actualLight && expected is LightTransaction expectedLight)
        {
            Assert.That(actualLight.BlobCellMask, Is.EqualTo(expectedLight.BlobCellMask), nameof(LightTransaction.BlobCellMask));
            Assert.That(actualLight.GetSparseBlobNetworkSize(), Is.EqualTo(expectedLight.GetSparseBlobNetworkSize()), nameof(LightTransaction.GetSparseBlobNetworkSize));
        }
    }

    private static void AssertByteArraysEquivalent(byte[][] actual, byte[][] expected)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected), nameof(Transaction.BlobVersionedHashes));
            return;
        }

        Assert.That(actual.Length, Is.EqualTo(expected.Length), nameof(Transaction.BlobVersionedHashes));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i], Is.EqualTo(expected[i]), $"{nameof(Transaction.BlobVersionedHashes)}[{i}]");
        }
    }

    private static bool IsExcluded(string[] excludedProperties, string propertyName) =>
        Array.IndexOf(excludedProperties, propertyName) >= 0;
}
