// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Newtonsoft.Json;

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
    public Keccak? TxHash { get; init; }

    public static GethTraceOptions Default { get; } = new();
}
