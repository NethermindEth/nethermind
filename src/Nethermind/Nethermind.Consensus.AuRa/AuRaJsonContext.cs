// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Metadata for the AuRa block results, which RPC methods return as their facade base types, and for the chain specification
/// and local files AuRa reads.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AuRaBlockForRpc))]
[JsonSerializable(typeof(AuRaBlockHeaderForRpc))]
[JsonSerializable(typeof(AuRaChainSpecLoader.AuRaGenesisSealJson))]
[JsonSerializable(typeof(Config.AuRaChainSpecEngineParameters))]
[JsonSerializable(typeof(Contracts.TxPriorityContract.LocalData))]
internal partial class AuRaJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.Facade);
}
