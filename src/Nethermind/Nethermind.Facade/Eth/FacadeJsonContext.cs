// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Filters;

namespace Nethermind.Facade.Eth;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BlockForRpc))]
[JsonSerializable(typeof(FilterLog))]
[JsonSerializable(typeof(TransactionForRpc))]
[JsonSerializable(typeof(SyncingResult))]
public partial class FacadeJsonContext : JsonSerializerContext;
