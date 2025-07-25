// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public record GethTraceOptions
{
    [JsonPropertyName("disableMemory")]
    [Obsolete("Use EnableMemory instead.")]
    public bool DisableMemory { get => !EnableMemory; init => EnableMemory = !value; }

    [JsonPropertyName("disableStorage")]
    public bool DisableStorage { get; init; }

    [JsonPropertyName("enableMemory")]
    public bool EnableMemory { get; init; }

    [JsonPropertyName("disableStack")]
    public bool DisableStack { get; init; }

    [JsonPropertyName("timeout")]
    public string Timeout { get; init; }

    [JsonPropertyName("tracer")]
    public string Tracer { get; init; }

    [JsonPropertyName("txHash")]
    public Hash256? TxHash { get; init; }

    [JsonPropertyName("tracerConfig")]
    public JsonElement? TracerConfig { get; init; }

    [JsonPropertyName("stateOverrides")]
    public Dictionary<Address, AccountOverride>? StateOverrides { get; init; }

    public static GethTraceOptions Default { get; } = new();
}
