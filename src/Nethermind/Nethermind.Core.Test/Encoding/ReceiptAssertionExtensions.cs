// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Core.Test.Encoding;

public static class ReceiptAssertionExtensions
{
    public static void AssertEquivalentTo(this TxReceipt? actual, TxReceipt? expected, params string[] excludedProperties)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected));
            return;
        }

        Assert.That(actual, Is.EqualTo(expected).UsingReceiptComparer(excludedProperties));
        actual.Logs.AssertEquivalentTo(expected.Logs);
    }

    public static void AssertEquivalentTo(this TxReceipt[]? actual, TxReceipt[]? expected, params string[] excludedProperties)
    {
        if (actual is null || expected is null)
        {
            Assert.That(actual, Is.EqualTo(expected));
            return;
        }

        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            actual[i].AssertEquivalentTo(expected[i], excludedProperties);
        }
    }

    public static void AssertEquivalentTo(this LogEntry[]? actual, LogEntry[]? expected)
        => Assert.That(actual, Is.EqualTo(expected).UsingPropertiesComparer<LogEntry>(static options => options));

    public static EqualConstraint UsingReceiptComparer(this EqualConstraint constraint, params string[] excludedProperties)
        => constraint
            .UsingPropertiesComparer<TxReceipt>(options =>
            {
                options = options.Excluding(static receipt => receipt.Logs);
                foreach (string excludedProperty in excludedProperties)
                {
                    options = excludedProperty switch
                    {
                        nameof(TxReceipt.Error) => options.Excluding(static receipt => receipt.Error),
                        _ => throw new ArgumentException($"Unknown TxReceipt property: {excludedProperty}", nameof(excludedProperties))
                    };
                }

                return options;
            });
}
