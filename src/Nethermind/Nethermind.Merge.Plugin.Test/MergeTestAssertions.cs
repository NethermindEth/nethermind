// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

internal static class MergeTestAssertions
{
    private static readonly EthereumJsonSerializer Serializer = new();

    public static void AssertJsonEquivalent<TActual, TExpected>(TActual actual, TExpected expected)
    {
        string actualJson = Serializer.Serialize(actual);
        string expectedJson = Serializer.Serialize(expected);

        Assert.That(actualJson, Is.EqualTo(expectedJson));
    }

    public static void AssertHeaderEquivalent(BlockHeader? actual, BlockHeader? expected)
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

        Assert.Multiple(() =>
        {
            Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            Assert.That(actual.Number, Is.EqualTo(expected.Number));
        });
    }
}
