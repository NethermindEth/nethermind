// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// <see cref="MustInitialize"/> is <see langword="true"/> so that an invalid tracing configuration (which makes
/// <see cref="OpcodeTraceRecorder.PrepareAsync"/> throw) aborts startup rather than silently running with tracing off.
/// </remarks>
public class StartOpcodeRetrospectiveTracing(OpcodeTraceRecorder recorder) : IStep
{
    public bool MustInitialize => true;

    public async Task Execute(CancellationToken cancellationToken)
    {
        // Validate the configuration up front; an invalid config throws and aborts startup.
        await recorder.PrepareAsync(cancellationToken);
        // Fire-and-forget: the background loop handles its own errors and shuts down via the recorder's disposal.
        _ = recorder.ExecuteTracingAsync();
    }
}
