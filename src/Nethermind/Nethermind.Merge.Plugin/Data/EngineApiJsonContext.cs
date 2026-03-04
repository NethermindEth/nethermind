// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.Data;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    IncludeFields = true)]
[JsonSerializable(typeof(ExecutionPayload))]
[JsonSerializable(typeof(ExecutionPayloadV3))]
[JsonSerializable(typeof(PayloadStatusV1))]
[JsonSerializable(typeof(byte[][]))]
[JsonSerializable(typeof(ForkchoiceStateV1))]
[JsonSerializable(typeof(ForkchoiceUpdatedV1Result))]
[JsonSerializable(typeof(PayloadAttributes))]
[JsonSerializable(typeof(BlobAndProofV1))]
[JsonSerializable(typeof(BlobAndProofV2))]
[JsonSerializable(typeof(BlobsBundleV1))]
[JsonSerializable(typeof(BlobsBundleV2))]
[JsonSerializable(typeof(GetPayloadV2Result))]
[JsonSerializable(typeof(GetPayloadV3Result))]
[JsonSerializable(typeof(GetPayloadV4Result))]
[JsonSerializable(typeof(GetPayloadV5Result))]
[JsonSerializable(typeof(GetBlobsHandlerV2Request))]
[JsonSerializable(typeof(ExecutionPayloadBodyV1Result))]
[JsonSerializable(typeof(TransitionConfigurationV1))]
[JsonSerializable(typeof(ClientVersionV1))]
internal partial class EngineApiJsonContext : JsonSerializerContext;
