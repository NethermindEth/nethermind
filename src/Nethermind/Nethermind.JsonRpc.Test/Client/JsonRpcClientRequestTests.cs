// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Client;

public class JsonRpcClientRequestTests
{
    [Test]
    public void Serializes_as_the_json_rpc_request_envelope()
    {
        // Remote nodes parse this request, so it must keep the envelope the client sent as an anonymous object.
        object?[] parameters = [TestItem.KeccakA, true, null, "latest"];
        EthereumJsonSerializer serializer = new();

        Assert.That(
            serializer.Serialize(new JsonRpcClientRequest("eth_call", parameters)),
            Is.EqualTo(serializer.Serialize(new { jsonrpc = "2.0", method = "eth_call", @params = (System.Collections.Generic.IEnumerable<object?>)parameters, id = 67 })));
    }
}