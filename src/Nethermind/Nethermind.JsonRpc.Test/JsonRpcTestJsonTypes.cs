// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;

namespace Nethermind.JsonRpc.Test;

/// <summary>Types the JSON-RPC test helpers serialize, for every assembly that uses them.</summary>
public static class JsonRpcTestJsonTypes
{
    /// <summary>Uncovered types only test code serializes, with the reason no production path needs them.</summary>
    public static readonly IReadOnlyDictionary<Type, string> TestOnly = new Dictionary<Type, string>
    {
        // Parameter types of a test RPC module; no production RPC method takes them.
        [typeof(JsonRpcContext)] = "test module parameter",
        [typeof(JsonRpcUrl)] = "test module parameter",
        [typeof(IJsonRpcDuplexClient)] = "test module parameter",
        [typeof(RpcEndpoint)] = "test module parameter",
        [typeof(IReadOnlySet<string>)] = "member of the test module's JsonRpcUrl parameter",
        // Shapes only the tests build: their JSON-RPC client's responses, batch requests, request parameters and round trips.
        [typeof(JsonRpcResponse<string>)] = "test client response",
        [typeof(JsonRpcResponse<byte[]>)] = "test client response",
        [typeof(JsonRpcResponse<SetCodeTransactionForRpc>)] = "test client response",
        [typeof(List<PeerInfo>)] = "test client response",
        [typeof(object[][])] = "test batch request",
        [typeof(Dictionary<Address, string[]>)] = "test request parameter",
        [typeof(Dictionary<Address, string>)] = "converter round trip",
        [typeof(Dictionary<AddressAsKey, string>)] = "converter round trip",
        [typeof(Dictionary<AddressAsKey, Dictionary<string, string>>)] = "converter round trip",
        [typeof(decimal)] = "converter round trip",
        [typeof(Dictionary<string, string>)] = "test comparison value",
    };
}
