// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Test.Data;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class TransactionForRpcTests : SerializationTestBase
{
    [Test]
    public void R_and_s_are_quantity_and_not_data()
    {
        byte[] r = new byte[32];
        byte[] s = new byte[32];
        r[1] = 1;
        s[2] = 2;

        Transaction tx = new()
        {
            Signature = new Signature(r, s, 27)
        };

        var txForRpc = TransactionForRpc.FromTransaction(tx);

        EthereumJsonSerializer serializer = new();
        string serialized = serializer.Serialize(txForRpc);

        var json = JObject.Parse(serialized);
        var expectedS = JObject.Parse("""{ "s": "0x20000000000000000000000000000000000000000000000000000000000"}""");
        var expectedR = JObject.Parse("""{ "r": "0x1000000000000000000000000000000000000000000000000000000000000"}""");

        json.Should().ContainSubtree(expectedS);
        json.Should().ContainSubtree(expectedR);
    }
}
