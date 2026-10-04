// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth;

/// <summary>Metadata for the values the facade's converters serialize through the options.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AccessListForRpc.Item))]
[JsonSerializable(typeof(IEnumerable<AccessListForRpc.Item>))]
[JsonSerializable(typeof(IEnumerable<UInt256>))]
[JsonSerializable(typeof(AuthorizationListForRpc.RpcAuthTuple))]
[JsonSerializable(typeof(IEnumerable<AuthorizationListForRpc.RpcAuthTuple>))]
[JsonSerializable(typeof(List<AuthorizationListForRpc.RpcAuthTuple>))]
[JsonSerializable(typeof(SyncingResultJsonConverter.Result))]
internal partial class FacadeConverterJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.Facade);
}