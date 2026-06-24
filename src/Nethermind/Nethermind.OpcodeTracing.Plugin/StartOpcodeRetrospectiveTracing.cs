// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.OpcodeTracing.Plugin.Tracing;

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Kicks off retrospective opcode tracing (historical block analysis). Registered only when the configured
/// tracing mode is Retrospective or RetrospectiveExecution.
/// </summary>
/// <remarks>
/// No step dependency is declared: the recorder reads the chain tip from <c>IBlockTree</c>, whose head is
/// loaded eagerly in its constructor, so the range is resolved correctly whenever this step runs.
/// </remarks>
public class StartOpcodeRetrospectiveTracing(OpcodeTraceRecorder recorder) : IStep
{
    public bool MustInitialize => false;

    public Task Execute(CancellationToken cancellationToken)
    {
        // Fire-and-forget: the background loop handles its own errors and shuts down via the recorder's disposal.
        if (recorder.Prepare()) _ = recorder.ExecuteTracingAsync();
        return Task.CompletedTask;
    }
}
