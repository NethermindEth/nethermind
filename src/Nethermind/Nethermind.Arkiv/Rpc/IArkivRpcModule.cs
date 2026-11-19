// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Arkiv.Rpc;

/// <summary>Arkiv's read namespace, as <c>arkiv-reth</c> serves it (<c>arkiv-reth-rpc</c>).</summary>
[RpcModule(ArkivRpcModule.ModuleName)]
public interface IArkivRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "An entity, as of the head or of a past block (this node archives every entity's history).", IsImplemented = true, IsSharable = true)]
    ResultWrapper<JsonElement?> arkiv_getEntity(Hash256 key, string? block = null);

    [JsonRpcMethod(Description = "Whether an entity record exists, expired or not.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<JsonElement?> arkiv_debugEntityExists(Hash256 key, string? block = null);

    [JsonRpcMethod(Description = "Runs an Arkiv query (the typed query language) at the head.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<JsonElement?> arkiv_query(string query, JsonElement? options = null);

    [JsonRpcMethod(Description = "Counts the live entities matching a query.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<JsonElement?> arkiv_getEntityCount(JsonElement? request = null);

    [JsonRpcMethod(Description = "The head block's number, timestamp and distance from its parent.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<JsonElement?> arkiv_getBlockTiming();
}
