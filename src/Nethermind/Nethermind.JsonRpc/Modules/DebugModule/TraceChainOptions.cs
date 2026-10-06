// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Tracing.GethStyle;

namespace Nethermind.JsonRpc.Modules.DebugModule;

// Geth TraceConfig, not TraceCallConfig: unknown call-only fields must not even be deserialized.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
internal sealed class TraceChainOptions
{
    public bool EnableMemory { get; init; }
    public bool DisableStack { get; init; }
    public bool DisableStorage { get; init; }
    public bool EnableReturnData { get; init; }
    [JsonConverter(typeof(GethTraceOptions.LimitConverter))]
    public long Limit { get; init; }
    public string? Tracer { get; init; }
    public string? Timeout { get; init; }
    public JsonElement? TracerConfig { get; init; }

    internal TimeSpan ParseTimeout() => Timeout is null ? TimeSpan.FromSeconds(5) : GoTraceDuration.Parse(Timeout);

    internal GethTraceOptions ToTraceOptions() => new()
    {
        EnableMemory = EnableMemory,
        DisableStack = DisableStack,
        DisableStorage = DisableStorage,
        EnableReturnData = EnableReturnData,
        Limit = Limit,
        Tracer = Tracer!,
        TracerConfig = TracerConfig
    };
}
