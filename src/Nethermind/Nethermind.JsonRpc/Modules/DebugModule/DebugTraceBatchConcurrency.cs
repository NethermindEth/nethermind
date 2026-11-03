// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>A traced call or transaction with a native tracer executes alongside its neighbours in a batch.</summary>
/// <remarks>A native trace is buffered, so the debug module it rents returns once it executes rather than once its
/// response is written, and no JavaScript engine runs. The debug module pool queues rather than rejects, so a window
/// wider than it would only leave items waiting for a module until the rental timeout; such a window keeps traces
/// sequential.</remarks>
public sealed class DebugTraceBatchConcurrency(IJsonRpcConfig jsonRpcConfig) : IBatchConcurrencyStrategy
{
    private const string DebugTraceCall = "debug_traceCall";
    private const string DebugTraceTransaction = "debug_traceTransaction";

    private readonly int _debugModuleInstances = jsonRpcConfig.GetDebugModuleConcurrentInstances();

    public IReadOnlyCollection<string> Methods { get; } = [DebugTraceCall, DebugTraceTransaction];

    public bool CanExecuteConcurrently(JsonRpcRequest request, int windowSize) =>
        windowSize <= _debugModuleInstances && UsesNativeTracer(request.Params, request.Method is DebugTraceTransaction ? 1 : 2);

    private static bool UsesNativeTracer(JsonElement parameters, int optionsIndex)
    {
        if (parameters.ValueKind != JsonValueKind.Array
            || parameters.GetArrayLength() <= optionsIndex
            || parameters[optionsIndex] is not { ValueKind: JsonValueKind.Object } options)
        {
            return false;
        }

        try
        {
            JsonElement? tracer = null;
            foreach (JsonProperty property in options.EnumerateObject())
            {
                ReadOnlySpan<byte> name = JsonMarshal.GetRawUtf8PropertyName(property);
                // An escaped name is not decoded here, so it could be any spelling of the tracer option.
                if (name.IndexOf("\\"u8) >= 0) return false;
                if (!Ascii.EqualsIgnoreCase(name, "tracer"u8)) continue;
                // The options binder matches names case-insensitively, so another spelling could select a different tracer.
                if (!name.SequenceEqual("tracer"u8) || tracer is not null) return false;
                tracer = property.Value;
            }

            return tracer is { ValueKind: JsonValueKind.String } value && GethLikeNativeTracerFactory.IsNativeTracer(value.GetString()!);
        }
        catch (InvalidOperationException)
        {
            // An invalid escape in the tracer value fails this item when it executes, not the whole batch here.
            return false;
        }
    }
}
