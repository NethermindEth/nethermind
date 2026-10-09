// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Json;
using Nethermind.Taiko.Precompiles;
using Nethermind.Taiko.Rpc;

namespace Nethermind.Taiko;

/// <summary>Metadata for the requests and responses Taiko exchanges with L1 through the JSON-RPC client.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonRpcResponse<DebugTraceCallResult>))]
[JsonSerializable(typeof(JsonRpcResponse<string>))]
[JsonSerializable(typeof(JsonRpcResponse<L1FeeHistoryResults>))]
[JsonSerializable(typeof(JsonObject))]
internal partial class L1ClientJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.External);
}
