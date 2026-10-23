// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>A traced call with a native tracer executes alongside its neighbours in a batch.</summary>
/// <remarks>A native trace is buffered, so the debug module it rents returns once it executes rather than once its
/// response is written, and no JavaScript engine runs. The debug module pool queues rather than rejects, so a window
/// wider than it would only leave items waiting for a module until the rental timeout; such a window keeps traced
/// calls sequential.</remarks>
public sealed class DebugTraceCallBatchConcurrency(IJsonRpcConfig jsonRpcConfig) : IBatchConcurrencyStrategy
{
    private readonly int _debugModuleInstances = jsonRpcConfig.GetDebugModuleConcurrentInstances();

    public IReadOnlyCollection<string> Methods { get; } = ["debug_traceCall"];

    public bool CanExecuteConcurrently(JsonRpcRequest request, int windowSize) =>
        windowSize <= _debugModuleInstances && UsesNativeTracer(request.Params);

    private static bool UsesNativeTracer(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Array
            || parameters.GetArrayLength() < 3
            || parameters[2] is not { ValueKind: JsonValueKind.Object } options)
        {
            return false;
        }

        try
        {
            JsonElement? tracer = null;
            foreach (JsonProperty property in options.EnumerateObject())
            {
                bool exact = property.NameEquals("tracer"u8);
                if (!exact && !property.Name.Equals("tracer", StringComparison.OrdinalIgnoreCase)) continue;
                // The options binder matches names case-insensitively, so another spelling could select a different tracer.
                if (!exact || tracer is not null) return false;
                tracer = property.Value;
            }

            return tracer is { ValueKind: JsonValueKind.String } name && GethLikeNativeTracerFactory.IsNativeTracer(name.GetString()!);
        }
        catch (InvalidOperationException)
        {
            // An invalid escape in a name or value fails this item when it executes, not the whole batch here.
            return false;
        }
    }
}
