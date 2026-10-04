// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Json;

namespace Nethermind.ExternalSigner.Plugin;

/// <summary>Metadata for the responses read from the Clef remote signer.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonRpcResponse<string>))]
[JsonSerializable(typeof(JsonRpcResponse<string[]>))]
[JsonSerializable(typeof(JsonRpcResponse<ClefWallet.SignTransactionResponse>))]
internal partial class ClefJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.External);
}