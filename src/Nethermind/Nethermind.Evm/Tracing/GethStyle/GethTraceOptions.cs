// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Evm.Tracing.GethStyle;

public record GethTraceOptions
{
    [JsonProperty("disableMemory")]
    [Obsolete("Use EnableMemory instead.")]
    public bool DisableMemory { get => !EnableMemory; init => EnableMemory = !value; }

    [JsonProperty("disableStack")]
    public bool DisableStack { get; init; }

    [JsonProperty("disableStorage")]
    public bool DisableStorage { get; init; }

    [JsonProperty("enableMemory")]
    public bool EnableMemory { get; init; }

    [JsonProperty("timeout")]
    public string Timeout { get; init; }

    [JsonProperty("tracer")]
    public string Tracer { get; init; }

    [JsonProperty("txHash")]
    public Hash256? TxHash { get; init; }

    [JsonProperty("tracerConfig")]
    public JRaw? TracerConfig { get; init; }

    public static GethTraceOptions Default { get; } = new();
}
