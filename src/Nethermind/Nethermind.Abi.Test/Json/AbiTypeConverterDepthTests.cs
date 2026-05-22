// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Nethermind.Blockchain.Contracts.Json;
using NUnit.Framework;

namespace Nethermind.Abi.Test.Json;

public class AbiTypeConverterDepthTests
{
    private static AbiType Deserialize(string type) =>
        JsonSerializer.Deserialize<AbiType>(
            $"\"{type}\"",
            new JsonSerializerOptions
            {
                Converters = { new AbiTypeConverter() }
            })!;

    private static string ArrayType(string baseType, int depth) =>
        new StringBuilder(baseType).Insert(baseType.Length, "[]", depth).ToString();

    [Test]
    public void Rejects_array_nesting_above_limit()
    {
        string payload = ArrayType("uint256", 33);

        Assert.Throws<AbiException>(() => Deserialize(payload));
    }

    [Test]
    public void Accepts_array_nesting_at_limit()
    {
        string payload = ArrayType("uint256", 32);

        AbiType result = Deserialize(payload);

        Assert.That(result, Is.Not.Null);
    }
}
