// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Xdc;

/// <summary>
/// Metadata for the XDC block and header results, which RPC methods return as their facade base types, forensic proofs and the XDC chain
/// specification parameters.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RPC.XdcBlockForRpc))]
[JsonSerializable(typeof(RPC.XdcSubnetBlockForRpc))]
[JsonSerializable(typeof(RPC.XdcBlockHeaderForRpc))]
[JsonSerializable(typeof(RPC.XdcSubnetBlockHeaderForRpc))]
[JsonSerializable(typeof(Types.ForensicsContent))]
[JsonSerializable(typeof(Types.VoteEquivocationContent))]
[JsonSerializable(typeof(Spec.XdcChainSpecEngineParameters))]
[JsonSerializable(typeof(Spec.XdcSubnetChainSpecEngineParameters))]
internal partial class XdcJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.Facade);
}
