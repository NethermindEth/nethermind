// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Test;
using NUnit.Framework;

/// <inheritdoc cref="JsonMetadataCoverageBase"/>
/// <remarks>Outside any namespace, so it covers every fixture in the assembly.</remarks>
[SetUpFixture]
internal class JsonMetadataCoverage() : JsonMetadataCoverageBase(JsonRpcTestJsonTypes.TestOnly, TestOnly)
{
    /// <summary>Shapes only these tests build: their JSON-RPC client's responses, batch requests, request parameters and round trips.</summary>
    private static readonly Dictionary<Type, string> TestOnly = new()
    {
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
