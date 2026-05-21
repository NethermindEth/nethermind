// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

}
