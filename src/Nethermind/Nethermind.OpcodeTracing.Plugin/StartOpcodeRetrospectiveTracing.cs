// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.OpcodeTracing.Plugin.Tracing;

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Kicks off retrospective opcode tracing (historical block analysis). Registered only when the configured
/// tracing mode is Retrospective or RetrospectiveExecution.
/// </summary>
public class StartOpcodeRetrospectiveTracing(OpcodeTraceRecorder recorder) : IStep
{
    // true so an invalid tracing config (PrepareAsync throws) aborts startup instead of being swallowed.
    public bool MustInitialize => true;

    public async Task Execute(CancellationToken cancellationToken)
    {
        await recorder.PrepareAsync(cancellationToken);
        // Fire-and-forget: the background loop owns its errors and tears down via the recorder's disposal.
        _ = recorder.ExecuteTracingAsync();
    }
}
