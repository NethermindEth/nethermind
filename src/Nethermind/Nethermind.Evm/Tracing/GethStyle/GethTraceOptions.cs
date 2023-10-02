// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle;

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
    public Keccak? TxHash { get; init; }

    public static GethTraceOptions Default { get; } = new();
}
