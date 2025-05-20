// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class MyTests
{
    [Test, Explicit]
    public void Test()
    {
        var policy = JsonNamingPolicy.CamelCase;
        var converted = policy.ConvertName("EIP1559Parameters");
        Console.WriteLine(converted);

        var jsonSerializer = new EthereumJsonSerializer();
        var obj = new
        {
            EIP1559Parameters = "Hello"
        };
        var serialized = jsonSerializer.Serialize(obj, indented: true);
        Console.WriteLine(serialized);
    }
}
