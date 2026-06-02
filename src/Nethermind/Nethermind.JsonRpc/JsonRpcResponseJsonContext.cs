// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcSuccessResponse))]
[JsonSerializable(typeof(JsonRpcErrorResponse))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(Error))]
public partial class JsonRpcResponseJsonContext : JsonSerializerContext;
