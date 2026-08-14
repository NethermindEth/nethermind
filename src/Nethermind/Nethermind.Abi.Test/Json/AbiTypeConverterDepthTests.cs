// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Text.Json;
using Nethermind.Blockchain.Contracts.Json;
using NUnit.Framework;

namespace Nethermind.Abi.Test.Json;

public class AbiTypeConverterDepthTests
{
    [TestCase(1024, false)]
    [TestCase(1025, true)]
    public void Enforces_array_nesting_limit(int depth, bool shouldThrow)
    {
        string payload = new StringBuilder("uint256").Insert("uint256".Length, "[]", depth).ToString();

        Action act = () => JsonSerializer.Deserialize<AbiType>(
            $"\"{payload}\"",
            new JsonSerializerOptions { Converters = { new AbiTypeConverter() } });

        if (shouldThrow)
            Assert.Throws<AbiException>(act);
        else
            Assert.DoesNotThrow(act);
    }
}
