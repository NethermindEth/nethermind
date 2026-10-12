// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.LightClient.Verifier;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TransactionForRpc))]
[JsonSerializable(typeof(LegacyTransactionForRpc))]
[JsonSerializable(typeof(AccessListTransactionForRpc))]
[JsonSerializable(typeof(EIP1559TransactionForRpc))]
[JsonSerializable(typeof(BlobTransactionForRpc))]
[JsonSerializable(typeof(SetCodeTransactionForRpc))]
[JsonSerializable(typeof(FrameTransactionForRpc))]
[JsonSerializable(typeof(FrameTransactionForRpc[]))]
[JsonSerializable(typeof(AccessListForRpc.Item))]
[JsonSerializable(typeof(IEnumerable<AccessListForRpc.Item>))]
[JsonSerializable(typeof(IEnumerable<UInt256>))]
[JsonSerializable(typeof(AuthorizationListForRpc.RpcAuthTuple))]
[JsonSerializable(typeof(IEnumerable<AuthorizationListForRpc.RpcAuthTuple>))]
[JsonSerializable(typeof(List<AuthorizationListForRpc.RpcAuthTuple>))]
internal partial class PortableTransactionJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers shared transaction metadata before verified calls deserialize input.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.Facade);
}
