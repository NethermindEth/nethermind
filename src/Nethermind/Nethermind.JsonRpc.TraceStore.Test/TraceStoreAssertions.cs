// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Tests;

internal static class TraceStoreAssertions
{
    private static readonly EthereumJsonSerializer Serializer = new();

    public static void AssertWrapper<T>(ResultWrapper<T> actual, ResultWrapper<T> expected)
    {
        Assert.That(actual, Is.EqualTo(expected)
            .UsingPropertiesComparer<ResultWrapper<T>>(
                static options => options.Excluding(static result => result.Data)));
        AssertJsonEquivalent(actual.Data, expected.Data);
    }

    public static void AssertJsonEquivalent<TActual, TExpected>(TActual actual, TExpected expected)
    {
        string actualJson = Serializer.Serialize(actual);
        string expectedJson = Serializer.Serialize(expected);

        Assert.That(actualJson, Is.EqualTo(expectedJson));
    }
}
