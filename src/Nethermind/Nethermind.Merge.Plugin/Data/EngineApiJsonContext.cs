// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
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
[JsonSerializable(typeof(PayloadAttributes))]
[JsonSerializable(typeof(GetBlobsHandlerV2Request))]
[JsonSerializable(typeof(ExecutionPayloadBodyV1Result))]
[JsonSerializable(typeof(TransitionConfigurationV1))]
[JsonSerializable(typeof(ClientVersionV1))]
[JsonSerializable(typeof(NewPayloadWithWitnessV1Result))]
[JsonSerializable(typeof(Witness))]
internal partial class EngineApiJsonContext : JsonSerializerContext;
