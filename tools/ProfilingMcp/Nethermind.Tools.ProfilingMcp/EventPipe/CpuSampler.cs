// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Nethermind.Tools.ProfilingMcp.EventPipe;

/// <summary>Records thread samples and runtime events of a live process to a <c>.nettrace</c> file over EventPipe.</summary>
/// <remarks>
/// Unlike an attached profiler, an EventPipe session can be started and stopped any number of times without affecting the process.
/// The sample profiler samples managed threads only, about every 10 ms, and records whether each was running managed code or was in
/// native code, which includes being blocked.
/// </remarks>
internal static class CpuSampler
{
    private const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    /// <summary>The keywords of <c>dotnet-trace</c>'s <c>dotnet-common</c> profile: GC, loader, JIT, exceptions, threading and IL-to-native maps.</summary>
    private const long RuntimeKeywords = 0x100003801D;

    public static Task CollectAsync(int pid, TimeSpan duration, string output, CancellationToken cancellationToken) =>
        RecordAsync(pid,
        [
            new(SampleProfilerProvider, EventLevel.Informational),
            new(ClrTraceEventParser.ProviderName, EventLevel.Informational, RuntimeKeywords),
        ], duration, output, cancellationToken);

    /// <summary>Records the providers' events, with the rundown that names JIT-compiled methods, to a <c>.nettrace</c> file.</summary>
    public static async Task RecordAsync(int pid, IReadOnlyList<EventPipeProvider> providers, TimeSpan duration, string output, CancellationToken cancellationToken)
    {
        DiagnosticsClient client = new(pid);
        using EventPipeSession session = await client.StartEventPipeSessionAsync(providers, requestRundown: true, token: cancellationToken);
        await using FileStream file = File.Create(output);
        Task copy = session.EventStream.CopyToAsync(file, CancellationToken.None);
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            // Stopping triggers the rundown that names the JIT-compiled methods, so it is needed even after cancellation.
            await session.StopAsync(CancellationToken.None);
            await copy;
        }
    }
}
