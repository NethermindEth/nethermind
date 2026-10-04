// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc.Test;

/// <summary>Types the JSON-RPC test helpers serialize, for every assembly that uses them.</summary>
public static class JsonRpcTestJsonTypes
{
    /// <summary>Uncovered types of the test RPC module's parameters; no production RPC method takes them.</summary>
    public static readonly IReadOnlyDictionary<Type, string> TestOnly = new Dictionary<Type, string>
    {
        [typeof(JsonRpcContext)] = "test module parameter",
        [typeof(JsonRpcUrl)] = "test module parameter",
        [typeof(IJsonRpcDuplexClient)] = "test module parameter",
        [typeof(RpcEndpoint)] = "test module parameter",
    };
}
