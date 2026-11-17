// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>A buffered transaction trace executes alongside its neighbours in a batch.</summary>
/// <remarks>A streamed trace executes while its response is written and keeps its trace module until then, so with
/// <see cref="IJsonRpcConfig.EnableTracingStreamMode"/> the window would only hold modules. The trace module pool queues
/// rather than rejects, so a window wider than it would only leave items waiting for a module until the rental timeout;
/// such a window keeps traces sequential.</remarks>
public sealed class TraceTransactionBatchConcurrency(IJsonRpcConfig jsonRpcConfig) : IBatchConcurrencyStrategy
{
    private readonly int _traceModuleInstances = jsonRpcConfig.GetTraceModuleConcurrentInstances();
    private readonly bool _buffered = !jsonRpcConfig.EnableTracingStreamMode;

    public IReadOnlyCollection<string> Methods { get; } = ["trace_transaction"];

    public bool CanExecuteConcurrently(JsonRpcRequest request, int windowSize) =>
        _buffered && windowSize <= _traceModuleInstances;
}
