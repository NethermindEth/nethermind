// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth;

/// <summary>Metadata for the facade RPC results and the values its converters serialize through the options.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BlockForRpc))]
[JsonSerializable(typeof(FilterLog))]
[JsonSerializable(typeof(TransactionForRpc))]
[JsonSerializable(typeof(LegacyTransactionForRpc))]
[JsonSerializable(typeof(AccessListTransactionForRpc))]
[JsonSerializable(typeof(EIP1559TransactionForRpc))]
[JsonSerializable(typeof(BlobTransactionForRpc))]
[JsonSerializable(typeof(SetCodeTransactionForRpc))]
[JsonSerializable(typeof(FrameTransactionForRpc))]
[JsonSerializable(typeof(FrameTransactionForRpc[]))]
[JsonSerializable(typeof(SyncingResult))]
[JsonSerializable(typeof(AccessListForRpc.Item))]
[JsonSerializable(typeof(IEnumerable<AccessListForRpc.Item>))]
[JsonSerializable(typeof(IEnumerable<UInt256>))]
[JsonSerializable(typeof(AuthorizationListForRpc.RpcAuthTuple))]
[JsonSerializable(typeof(IEnumerable<AuthorizationListForRpc.RpcAuthTuple>))]
[JsonSerializable(typeof(List<AuthorizationListForRpc.RpcAuthTuple>))]
[JsonSerializable(typeof(SyncingResultJsonConverter.Result))]
internal partial class FacadeJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.Facade);
}
