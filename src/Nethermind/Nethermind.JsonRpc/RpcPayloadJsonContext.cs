// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc;

/// <summary>
/// Metadata for JSON-RPC payloads that method signatures do not name: <c>eth_config</c> builds its result as nodes, proofs are
/// written by a converter, peer protocols are stored as <see cref="object"/>, and diagnostics wrap responses.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ForkConfigSummary))]
[JsonSerializable(typeof(ForkConfig))]
[JsonSerializable(typeof(BlobScheduleSettingsForRpc))]
[JsonSerializable(typeof(OrderedDictionary<string, Address>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(StorageProof))]
[JsonSerializable(typeof(StorageProof[]))]
[JsonSerializable(typeof(ReadOnlyMemory<byte>?))]
[JsonSerializable(typeof(JsonRpcDiagnostics.DiagnosticJsonRpcResult))]
[JsonSerializable(typeof(RpcReport))]
[JsonSerializable(typeof(Modules.Admin.PeerInfo.ProtocolVersion))]
internal partial class RpcPayloadJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.EthRpc);
}