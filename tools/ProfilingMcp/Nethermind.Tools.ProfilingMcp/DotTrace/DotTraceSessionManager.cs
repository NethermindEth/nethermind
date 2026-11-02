// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.Tools.ProfilingMcp.DotTrace;

/// <summary>
/// Owns the dotTrace profiling sessions started by the server and disconnects them on shutdown.
/// </summary>
public sealed class DotTraceSessionManager(DotTraceLocator locator, WorkspacePaths paths) : IAsyncDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DotTraceSession> _sessions = new();
    private int _nextId;

    public async Task<DotTraceSession> StartAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string profilingType,
        bool collectFromStart,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        List<string> profilerArgs = ["start"];
        AddCommonArguments(profilerArgs, profilingType, collectFromStart, timeoutSeconds, out string snapshotDir, Path.GetFileNameWithoutExtension(executable));
        if (workingDirectory is not null)
            profilerArgs.Add($"--work-dir={workingDirectory}");

        // A double dash makes the profiler treat everything after it as the target command line.
        profilerArgs.Add("--");
        profilerArgs.Add(executable);
        profilerArgs.AddRange(arguments);

        string description = $"start {executable} {string.Join(' ', arguments)}".TrimEnd();
        return await LaunchAsync(description, profilingType, snapshotDir, canDetach: true, profilerArgs, cancellationToken);
    }

    public async Task<DotTraceSession> AttachAsync(int pid, string profilingType, bool collectFromStart, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        string processName;
        try
        {
            using Process target = Process.GetProcessById(pid);
            processName = target.ProcessName;
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"No process with PID {pid} is running.", nameof(pid));
        }

        // See DotTraceSession.DisconnectAsync: detaching from an attached process on Linux crashes it, and dotTrace's own --timeout detaches.
        bool canDetach = OperatingSystem.IsWindows();
        if (!canDetach && timeoutSeconds is > 0)
            throw new ArgumentException("timeoutSeconds is not supported for attach on this OS: dotTrace detaches on timeout, which crashes the target. Call trace_snapshot and trace_stop instead.", nameof(timeoutSeconds));

        List<string> profilerArgs = ["attach", pid.ToString()];
        AddCommonArguments(profilerArgs, profilingType, collectFromStart, timeoutSeconds, out string snapshotDir, processName);
        return await LaunchAsync($"attach {pid} ({processName})", profilingType, snapshotDir, canDetach, profilerArgs, cancellationToken);
    }

    public DotTraceSession Get(string id) =>
        _sessions.TryGetValue(id, out DotTraceSession? session)
            ? session
            : throw new ArgumentException($"Unknown session '{id}'. Known sessions: {string.Join(", ", _sessions.Keys)}", nameof(id));

    public IReadOnlyCollection<DotTraceSession> Sessions => [.. _sessions.Values];

    public async Task RemoveAsync(string id)
    {
        if (_sessions.TryRemove(id, out DotTraceSession? session))
            await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (DotTraceSession session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
    }

    private void AddCommonArguments(List<string> args, string profilingType, bool collectFromStart, int? timeoutSeconds, out string snapshotDir, string label)
    {
        snapshotDir = Path.Combine(paths.Snapshots, Path.GetFileNameWithoutExtension(WorkspacePaths.NewFileName(label, "")));
        Directory.CreateDirectory(snapshotDir);

        args.Add($"--profiling-type={profilingType}");
        args.Add($"--save-to={snapshotDir}");
        args.Add("--service-input=stdin");
        args.Add("--service-output=on");
        args.Add($"--collect-data-from-start={(collectFromStart ? "on" : "off")}");
        args.Add("--no-check-for-updates");
        if (timeoutSeconds is > 0)
            args.Add($"--timeout={timeoutSeconds}s");
    }

    private async Task<DotTraceSession> LaunchAsync(string description, string profilingType, string snapshotDir, bool canDetach, List<string> profilerArgs, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(await locator.GetProfilerPathAsync(cancellationToken));
        foreach (string arg in profilerArgs)
            startInfo.ArgumentList.Add(arg);

        string id = Interlocked.Increment(ref _nextId).ToString();
        DotTraceSession session = DotTraceSession.Launch(id, description, profilingType, snapshotDir, canDetach, startInfo);
        _sessions[id] = session;

        // Wait until the profiler has hooked the target so that the first snapshot request is not lost.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadyTimeout);
        while (session.ProfiledPids.Count == 0 && session.State != DotTraceSessionState.Exited)
        {
            try
            {
                await Task.Delay(100, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (session.State is DotTraceSessionState.Exited && session.ProfiledPids.Count == 0)
        {
            _sessions.TryRemove(id, out _);
            throw new InvalidOperationException($"The profiler exited with code {session.ExitCode} without connecting to the target.\n{session.FormatOutputTail()}");
        }

        return session;
    }
}
