// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL.L1Bridge;

/// <summary>Metadata for the responses the L1 bridge reads through the JSON-RPC client.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonRpcResponse<ReceiptForRpc[]>))]
[JsonSerializable(typeof(JsonRpcResponse<L1Block?>))]
[JsonSerializable(typeof(JsonRpcResponse<ulong?>))]
internal partial class L1ClientJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.External);
}