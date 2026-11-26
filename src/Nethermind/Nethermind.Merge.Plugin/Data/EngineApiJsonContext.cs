// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Data;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    IncludeFields = true,
    Converters = new[] { typeof(ByteArrayArrayConverter) })]
[JsonSerializable(typeof(ExecutionPayload))]
[JsonSerializable(typeof(ExecutionPayloadV3))]
[JsonSerializable(typeof(ExecutionPayloadV4))]
[JsonSerializable(typeof(PayloadStatusV1))]
[JsonSerializable(typeof(PayloadStatusV2))]
[JsonSerializable(typeof(byte[][]))]
[JsonSerializable(typeof(ForkchoiceStateV1))]
[JsonSerializable(typeof(ForkchoiceUpdatedV1Result))]
[JsonSerializable(typeof(PayloadAttributes))]
[JsonSerializable(typeof(BlobAndProofV1))]
[JsonSerializable(typeof(BlobAndProofV2))]
[JsonSerializable(typeof(BlobAndProofV2?[]))]
[JsonSerializable(typeof(BlobsV1DirectResponse))]
[JsonSerializable(typeof(BlobsV2DirectResponse))]
[JsonSerializable(typeof(PayloadBodiesV1DirectResponse))]
[JsonSerializable(typeof(PayloadBodiesV2DirectResponse))]
[JsonSerializable(typeof(BlobCellsAndProofs))]
[JsonSerializable(typeof(BlobsBundleV1))]
[JsonSerializable(typeof(BlobsBundleV2))]
[JsonSerializable(typeof(GetPayloadV2Result))]
[JsonSerializable(typeof(GetPayloadV3Result))]
[JsonSerializable(typeof(GetPayloadV4Result))]
[JsonSerializable(typeof(GetPayloadV5Result))]
[JsonSerializable(typeof(GetBlobsHandlerV2Request))]
[JsonSerializable(typeof(GetBlobsHandlerV4Request))]
[JsonSerializable(typeof(ExecutionPayloadBodyV1Result))]
[JsonSerializable(typeof(TransitionConfigurationV1))]
[JsonSerializable(typeof(ClientVersionV1))]
[JsonSerializable(typeof(NewPayloadWithWitnessV1Result))]
[JsonSerializable(typeof(Witness))]
[JsonSerializable(typeof(BoostPayloadAttributes))]
[JsonSerializable(typeof(BoostExecutionPayloadV1))]
internal partial class EngineApiJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.EngineApi);
}
