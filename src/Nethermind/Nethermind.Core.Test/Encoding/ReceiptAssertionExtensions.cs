// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public static class ReceiptAssertionExtensions
{
    public static void AssertEquivalentTo(this TxReceipt? actual, TxReceipt? expected)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected));
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.TxType, Is.EqualTo(expected.TxType));
            Assert.That(actual.StatusCode, Is.EqualTo(expected.StatusCode));
            Assert.That(actual.BlockNumber, Is.EqualTo(expected.BlockNumber));
            Assert.That(actual.BlockHash, Is.EqualTo(expected.BlockHash));
            Assert.That(actual.TxHash, Is.EqualTo(expected.TxHash));
            Assert.That(actual.Index, Is.EqualTo(expected.Index));
            Assert.That(actual.GasUsed, Is.EqualTo(expected.GasUsed));
            Assert.That(actual.GasUsedTotal, Is.EqualTo(expected.GasUsedTotal));
            Assert.That(actual.BlockGasUsed, Is.EqualTo(expected.BlockGasUsed));
            Assert.That(actual.StorageGasUsed, Is.EqualTo(expected.StorageGasUsed));
            Assert.That(actual.ExecutionGasUsed, Is.EqualTo(expected.ExecutionGasUsed));
            Assert.That(actual.EffectiveGasPrice, Is.EqualTo(expected.EffectiveGasPrice));
            Assert.That(actual.Sender, Is.EqualTo(expected.Sender));
            Assert.That(actual.ContractAddress, Is.EqualTo(expected.ContractAddress));
            Assert.That(actual.Recipient, Is.EqualTo(expected.Recipient));
            Assert.That(actual.ReturnValue, Is.EqualTo(expected.ReturnValue));
            Assert.That(actual.PostTransactionState, Is.EqualTo(expected.PostTransactionState));
            Assert.That(actual.Bloom, Is.EqualTo(expected.Bloom));
            Assert.That(actual.Error, Is.EqualTo(expected.Error));
        });

        actual.Logs.AssertEquivalentTo(expected.Logs);
    }

    public static void AssertEquivalentTo(this TxReceipt[]? actual, TxReceipt[]? expected)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected));
            return;
        }

        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            actual[i].AssertEquivalentTo(expected[i]);
        }
    }

    private static void AssertEquivalentTo(this LogEntry[]? actual, LogEntry[]? expected)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected));
            return;
        }

        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual[i].Address, Is.EqualTo(expected[i].Address));
                Assert.That(actual[i].Data, Is.EqualTo(expected[i].Data));
                Assert.That(actual[i].Topics, Is.EqualTo(expected[i].Topics));
            });
        }
    }
}
