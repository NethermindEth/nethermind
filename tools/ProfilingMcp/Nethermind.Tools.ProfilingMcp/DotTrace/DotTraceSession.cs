// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;

namespace Nethermind.Tools.ProfilingMcp.DotTrace;

public enum DotTraceSessionState
{
    Starting,
    Collecting,
    Stopped,
    Exited,
}

/// <summary>
/// A running <c>dottrace start</c> or <c>dottrace attach</c> process controlled through service messages.
/// </summary>
public sealed class DotTraceSession : IAsyncDisposable
{
    private const int OutputTailLines = 40;

    private readonly Process _process;
    private readonly Lock _gate = new();
    private readonly Queue<string> _outputTail = new();
    private readonly List<string> _snapshots = [];
    private readonly HashSet<int> _profiledPids = [];
    private TaskCompletionSource<string>? _pendingSnapshot;

    private static readonly TimeSpan StopCollectingTimeout = TimeSpan.FromSeconds(15);

    private readonly bool _canDetach;

    private DotTraceSession(string id, string description, string profilingType, string snapshotDir, bool canDetach, Process process)
    {
        Id = id;
        Description = description;
        ProfilingType = profilingType;
        SnapshotDir = snapshotDir;
        _canDetach = canDetach;
        _process = process;
    }

    public string Id { get; }

    public string Description { get; }

    public string ProfilingType { get; }

    public string SnapshotDir { get; }

    public DotTraceSessionState State { get; private set; } = DotTraceSessionState.Starting;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <param name="canDetach">False when detaching the profiler would crash the target; the session then ends by killing the profiler instead.</param>
    internal static DotTraceSession Launch(string id, string description, string profilingType, string snapshotDir, bool canDetach, ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.StandardOutputEncoding = Encoding.UTF8;

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        DotTraceSession session = new(id, description, profilingType, snapshotDir, canDetach, process);
        process.OutputDataReceived += (_, e) => session.OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => session.OnOutput(e.Data);
        process.Exited += (_, _) => session.OnExited();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return session;
    }

    /// <summary>Saves a snapshot of the data collected so far.</summary>
    /// <param name="resume">Restart collection after saving, because the profiler stops collecting when it takes a snapshot.</param>
    /// <param name="timeout">How long to wait for the profiler to write the snapshot.</param>
    /// <returns>Path of the snapshot index file (<c>*.dtp</c> or <c>*.dtt</c>).</returns>
    public async Task<string> TakeSnapshotAsync(bool resume, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<string> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (State is DotTraceSessionState.Exited)
                throw new InvalidOperationException($"Session {Id} has exited. {FormatOutputTail()}");
            if (_pendingSnapshot is not null)
                throw new InvalidOperationException($"Session {Id} is already taking a snapshot.");
            _pendingSnapshot = pending;
        }

        try
        {
            await SendAsync("get-snapshot");
            string snapshot = await pending.Task.WaitAsync(timeout, cancellationToken);
            if (resume)
                await SendAsync("start");
            return snapshot;
        }
        finally
        {
            lock (_gate)
                _pendingSnapshot = null;
        }
    }

    public Task StartCollectingAsync() => SendAsync("start");

    public Task DropAsync() => SendAsync("drop");

    /// <summary>Ends the session; a process launched by <c>dottrace start</c> is killed, an attached process keeps running.</summary>
    /// <remarks>
    /// When the session cannot detach, collection is stopped and the profiler process is killed, leaving the idle profiler
    /// loaded in the target until it restarts. Detaching a Sampling profiler from an attached .NET 10 process on Linux
    /// (dotTrace 2026.2.3.1) crashes the target with SIGSEGV, while killing the profiler after collection stops does not.
    /// </remarks>
    public async Task DisconnectAsync(TimeSpan timeout)
    {
        if (_process.HasExited)
            return;

        if (!_canDetach)
        {
            if (State is DotTraceSessionState.Collecting)
            {
                await SendAsync("drop");
                using CancellationTokenSource stopped = new(StopCollectingTimeout);
                while (State is DotTraceSessionState.Collecting && !stopped.IsCancellationRequested)
                    await Task.Delay(100, CancellationToken.None);
            }

            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            return;
        }

        await SendAsync("disconnect");
        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public IReadOnlyList<string> Snapshots
    {
        get
        {
            lock (_gate)
                return [.. _snapshots];
        }
    }

    public IReadOnlyCollection<int> ProfiledPids
    {
        get
        {
            lock (_gate)
                return [.. _profiledPids];
        }
    }

    public string FormatOutputTail()
    {
        lock (_gate)
            return _outputTail.Count == 0 ? "No profiler output." : "Profiler output (tail):\n" + string.Join('\n', _outputTail);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(TimeSpan.FromSeconds(30));
        _process.Dispose();
    }

    private async Task SendAsync(string command)
    {
        if (_process.HasExited)
            throw new InvalidOperationException($"Session {Id} has exited. {FormatOutputTail()}");

        await _process.StandardInput.WriteAsync(ServiceMessage.FormatCommand(command, pid: null));
        await _process.StandardInput.FlushAsync();
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_gate)
        {
            _outputTail.Enqueue(line.TrimStart('﻿'));
            if (_outputTail.Count > OutputTailLines)
                _outputTail.Dequeue();

            if (!ServiceMessage.TryParse(line, out ServiceMessage message))
                return;

            switch (message.Name)
            {
                case "connected" when message.Pid is int pid:
                    _profiledPids.Add(pid);
                    break;
                case "started":
                    State = DotTraceSessionState.Collecting;
                    break;
                case "stopped":
                    State = DotTraceSessionState.Stopped;
                    break;
                case "snapshot-saved" when message.Path is not null:
                    _snapshots.Add(message.Path);
                    _pendingSnapshot?.TrySetResult(message.Path);
                    break;
            }
        }
    }

    private void OnExited()
    {
        lock (_gate)
        {
            State = DotTraceSessionState.Exited;
            _pendingSnapshot?.TrySetException(new InvalidOperationException($"Profiler exited with code {_process.ExitCode} before saving a snapshot. {FormatOutputTailUnlocked()}"));
        }
    }

    private string FormatOutputTailUnlocked() => string.Join('\n', _outputTail);
}
