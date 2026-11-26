// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism;

/// <summary>
/// Metadata for deposit transactions and receipts, which RPC methods expose as their base types, sequencer responses and
/// the Optimism chain specification parameters.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Rpc.DepositTransactionForRpc))]
[JsonSerializable(typeof(Rpc.OptimismReceiptForRpc))]
[JsonSerializable(typeof(Nethermind.JsonRpc.Data.ReceiptsForRpc<Rpc.OptimismReceiptForRpc>))]
[JsonSerializable(typeof(Nethermind.JsonRpc.Client.JsonRpcResponse<Nethermind.Core.Crypto.Hash256>))]
[JsonSerializable(typeof(OptimismChainSpecEngineParameters))]
[JsonSerializable(typeof(CL.CLChainSpecEngineParameters))]
internal partial class OptimismJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.Facade);
}
