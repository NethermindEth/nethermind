// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>Metadata for the chain specification and genesis files the node loads at startup.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ChainSpecJson))]
[JsonSerializable(typeof(GethGenesisJson))]
[JsonSerializable(typeof(ulong))]
internal partial class ChainSpecJsonContext : JsonSerializerContext
{
#if !ZK_EVM
    // The zkEVM guest serializes no JSON; registering would build the serializer at its startup.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.External);
#endif
}
