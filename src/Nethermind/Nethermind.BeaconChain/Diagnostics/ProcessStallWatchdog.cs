// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Diagnostics;

/// <summary>Reports delays in dedicated-thread execution and thread-pool work.</summary>
public sealed class ProcessStallWatchdog(ILogManager logManager) : IDisposable
{
    private static readonly Action<Probe> ProbeCallback = static probe => probe.Owner.CompleteProbe(probe);
    private readonly ILogger _logger = logManager.GetClassLogger<ProcessStallWatchdog>();
    private readonly ManualResetEvent _stop = new(false);
    private readonly Lock _lifecycleLock = new();
    private Thread? _thread;
    private bool _disposed;
    private bool _initialized;
    private TimeSpan _previousTick;
    private RuntimeSample _previousSample;
    private TimeSpan _summaryTime;
    private TimeSpan _summaryPause;
    private TimeSpan _maximumTickDelay;
    private long _maximumProbeTicks;
    private bool _probePending;
    private TimeSpan _probeQueuedAt;
    private long _probeCompletedAt = -1;
    private long _completedAtQueue;
    private bool _reportedWait;

    internal TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan StallThreshold { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan SummaryInterval { get; init; } = TimeSpan.FromMinutes(1);
    internal Func<TimeSpan> Clock { get; init; } = static () => Stopwatch.GetElapsedTime(0);
    internal Func<RuntimeSample> Sample { get; init; } = static () => new(
        GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
        ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount, ThreadPool.CompletedWorkItemCount);
    internal Func<WaitHandle, TimeSpan, bool> WaitForTick { get; init; } = static (stop, interval) => stop.WaitOne(interval);
    internal Action<Action<Probe>, Probe> ScheduleProbe { get; init; } = static (callback, probe) =>
        ThreadPool.UnsafeQueueUserWorkItem(callback, probe, preferLocal: false);
    internal bool IsRunning => _thread?.IsAlive == true;

    /// <summary>Starts a dedicated background thread once.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Beacon process stall watchdog" };
            _thread.Start();
        }
    }

    private void Run()
    {
        while (!_stop.WaitOne(0))
        {
            try
            {
                Tick();
                if (WaitForTick(_stop, TickInterval)) return;
            }
            catch (Exception)
            {
                ReportFailure();
                if (_stop.WaitOne(TickInterval)) return;
            }
        }
    }

    internal void Tick()
    {
        TimeSpan now = Clock();
        RuntimeSample sample = Sample();
        if (!_initialized)
        {
            _previousTick = _summaryTime = now;
            _previousSample = sample;
            _summaryPause = sample.Pause;
            _initialized = true;
        }

        TimeSpan elapsed = now - _previousTick;
        RuntimeSample previous = _previousSample;
        _previousTick = now;
        _previousSample = sample;
        _maximumTickDelay = elapsed > _maximumTickDelay ? elapsed : _maximumTickDelay;
        if (elapsed > StallThreshold && _logger.IsInfo)
        {
            _logger.Info(FormattableString.Invariant($"Process paused, the watchdog thread did not run for {elapsed.TotalSeconds:F1} s; GC pause {(sample.Pause - previous.Pause).TotalSeconds:F3} s; GC collections gen0 {sample.Gen0 - previous.Gen0}, gen1 {sample.Gen1 - previous.Gen1}, gen2 {sample.Gen2 - previous.Gen2}; gen2 collected: {sample.Gen2 != previous.Gen2}"));
        }

        if (_probePending)
        {
            long completed = Interlocked.Read(ref _probeCompletedAt);
            TimeSpan waited = (completed < 0 ? now : TimeSpan.FromTicks(completed)) - _probeQueuedAt;
            if (waited > StallThreshold && !_reportedWait)
            {
                _reportedWait = true;
                if (_logger.IsInfo) _logger.Info(FormattableString.Invariant($"Thread pool starved, queued work waited {waited.TotalSeconds:F1} s; pending work items {sample.Pending}; thread count {sample.Threads}; completed work items delta {sample.Completed - _completedAtQueue}"));
            }

            if (completed >= 0)
            {
                if (_reportedWait && _logger.IsInfo) _logger.Info(FormattableString.Invariant($"Thread pool starvation ended after {waited.TotalSeconds:F1} s"));
                _probePending = false;
                _reportedWait = false;
            }
        }

        if (now - _summaryTime >= SummaryInterval)
        {
            long maximumProbeTicks = Interlocked.Exchange(ref _maximumProbeTicks, 0);
            if (_logger.IsDebug) _logger.Debug(FormattableString.Invariant($"Process stall watchdog summary: max tick delay {_maximumTickDelay.TotalSeconds:F3} s; max probe latency {TimeSpan.FromTicks(maximumProbeTicks).TotalSeconds:F3} s; pending work items {sample.Pending}; thread count {sample.Threads}; GC pause {(sample.Pause - _summaryPause).TotalSeconds:F3} s; working set {Environment.WorkingSet} bytes"));
            _summaryTime = now;
            _summaryPause = sample.Pause;
            _maximumTickDelay = TimeSpan.Zero;
        }

        bool tracked = !_probePending;
        if (tracked)
        {
            _probeQueuedAt = now;
            _completedAtQueue = sample.Completed;
            Interlocked.Exchange(ref _probeCompletedAt, -1);
            _probePending = true;
        }

        try
        {
            ScheduleProbe(ProbeCallback, new Probe(this, now, tracked));
        }
        catch
        {
            if (tracked) _probePending = false;
            throw;
        }
    }

    private void CompleteProbe(Probe probe)
    {
        try
        {
            TimeSpan now = Clock();
            long latency = (now - probe.QueuedAt).Ticks;
            long maximum = Interlocked.Read(ref _maximumProbeTicks);
            while (latency > maximum)
            {
                long observed = Interlocked.CompareExchange(ref _maximumProbeTicks, latency, maximum);
                if (observed == maximum) break;
                maximum = observed;
            }
            if (probe.Tracked) Interlocked.Exchange(ref _probeCompletedAt, now.Ticks);
        }
        catch (Exception)
        {
            ReportFailure();
        }
    }

    private void ReportFailure()
    {
        try
        {
            if (_logger.IsDebug) _logger.Debug("Process stall watchdog tick failed.");
        }
        catch (Exception)
        {
            // A failing logger must not terminate the diagnostic thread.
        }
    }

    /// <summary>Signals the thread and waits for it to stop; safe to call repeatedly.</summary>
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            _thread?.Join();
            _stop.Dispose();
        }
    }

    internal readonly record struct Probe(ProcessStallWatchdog Owner, TimeSpan QueuedAt, bool Tracked);
    internal readonly record struct RuntimeSample(TimeSpan Pause, int Gen0, int Gen1, int Gen2, long Pending, int Threads, long Completed);
}
