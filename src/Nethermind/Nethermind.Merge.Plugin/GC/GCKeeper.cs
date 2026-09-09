// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.GC;

using Nethermind.Core.Extensions;

public class GCKeeper : IDisposable
{
    private static ulong _forcedGcCount = 0;
    private static long _lastGcTimeMs;
    // The runtime splits totalSize as soh = total - loh; when lohSize is omitted it budgets the
    // full totalSize for LOH as well, committing that much LOH inside the per-call EE suspension.
    private static readonly long _lohSize = 64.MB;
    private static readonly long _defaultSize = 512.MB + _lohSize;
    // Starting a region takes well under a millisecond unless GCHeap::StartNoGCRegion is waiting for an
    // in-flight background gen2 collection to finish, which on a large heap is seconds of engine API
    // latency; a payload that does not get its region by then runs without one.
    internal static readonly TimeSpan RegionStartWaitBound = TimeSpan.FromMilliseconds(20);

    private readonly Lock _lock = new();
    private readonly Lock _regionLock = new();
    private readonly IGCStrategy _gcStrategy;
    private readonly IGCRuntime _runtime;
    private readonly int _postBlockDelayMs;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _startRequested = new(0);
    private readonly ManualResetEventSlim _startCompleted = new();
    private Task _gcScheduleTask = Task.CompletedTask;
    private CancellationTokenSource? _shutdownCts = new();
    private Thread? _regionStarter;
    private bool _startPending;
    private bool _payloadActive;
    private bool _regionHeldForPayload;
    private FailCause _startResult;

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager) : this(gcStrategy, logManager, GCRuntime.Instance)
    {
    }

    internal GCKeeper(IGCStrategy gcStrategy, ILogManager logManager, IGCRuntime runtime)
    {
        _gcStrategy = gcStrategy;
        _runtime = runtime;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _logger = logManager.GetClassLogger<GCKeeper>();
    }

    public void Dispose() => CancellationTokenExtensions.CancelDisposeAndClear(ref _shutdownCts);

    /// <summary>Brackets a payload's processing with a no-GC region when one can be started promptly.</summary>
    /// <remarks>
    /// The region is started on a dedicated thread and the payload waits at most <see cref="RegionStartWaitBound"/>
    /// for it. A start still pending after that is waiting for a background gen2 collection: the payload runs
    /// without a region, adopts it if it lands before the payload finishes, and otherwise the keeper ends it as
    /// soon as it starts. Only one start is ever pending; later payloads reuse it instead of queueing another.
    /// </remarks>
    public IDisposable TryStartNoGCRegion()
    {
        long size = _defaultSize;
        bool pausedGCScheduler = GCScheduler.MarkGCPaused();
        if (!_gcStrategy.CanStartNoGCRegion())
        {
            return new NoGCRegion(this, FailCause.StrategyDisallowed, size, pausedGCScheduler, _logger);
        }

        lock (_regionLock)
        {
            _payloadActive = true;
            if (_startPending)
            {
                Metrics.NoGCRegionStartsDeferred++;
                return new NoGCRegion(this, FailCause.StartDeferred, size, pausedGCScheduler, _logger);
            }

            _startPending = true;
            _startCompleted.Reset();
        }

        EnsureRegionStarter();
        _startRequested.Release();
        FailCause failCause = _startCompleted.Wait(RegionStartWaitBound) ? _startResult : FailCause.StartDeferred;
        if (failCause == FailCause.StartDeferred) Metrics.NoGCRegionStartsDeferred++;
        return new NoGCRegion(this, failCause, size, pausedGCScheduler, _logger);
    }

    private void EnsureRegionStarter()
    {
        if (_regionStarter is not null) return;

        Thread starter = new(RunRegionStarter) { IsBackground = true, Name = "GC region starter" };
        if (Interlocked.CompareExchange(ref _regionStarter, starter, null) is null)
        {
            starter.Start();
        }
    }

    private void RunRegionStarter()
    {
        CancellationToken token = _shutdownCts?.Token ?? CancellationToken.None;
        try
        {
            while (true)
            {
                _startRequested.Wait(token);
                FailCause failCause = StartRegion();
                lock (_regionLock)
                {
                    _startPending = false;
                    _startResult = failCause;
                    if (failCause == FailCause.None)
                    {
                        if (_payloadActive)
                        {
                            _regionHeldForPayload = true;
                        }
                        else
                        {
                            // The payload that asked for it has finished; nothing is left to protect.
                            EndRegion();
                        }
                    }
                    _startCompleted.Set();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private FailCause StartRegion()
    {
        try
        {
            return _runtime.TryStartNoGCRegion(_defaultSize, _lohSize) ? FailCause.None : FailCause.GCFailedToStartNoGCRegion;
        }
        catch (ArgumentOutOfRangeException)
        {
            return FailCause.TotalSizeExceededTheEphemeralSegmentSize;
        }
        catch (InvalidOperationException)
        {
            return FailCause.AlreadyInNoGCRegion;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"{nameof(System.GC.TryStartNoGCRegion)} failed with exception.", e);
            return FailCause.Exception;
        }
    }

    private void OnPayloadDone()
    {
        lock (_regionLock)
        {
            _payloadActive = false;
            if (_regionHeldForPayload)
            {
                _regionHeldForPayload = false;
                EndRegion();
            }
        }
    }

    private void EndRegion()
    {
        if (_runtime.IsInNoGCRegion)
        {
            try
            {
                _runtime.EndNoGCRegion();
                ScheduleGC();
            }
            catch (InvalidOperationException)
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with Exception with {_defaultSize} bytes");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"{nameof(System.GC.EndNoGCRegion)} failed with exception.", e);
            }
        }
        else if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with {_defaultSize} bytes");
    }

    private enum FailCause
    {
        None,
        StrategyDisallowed,
        StartDeferred,
        GCFailedToStartNoGCRegion,
        TotalSizeExceededTheEphemeralSegmentSize,
        AlreadyInNoGCRegion,
        Exception
    }

    private class NoGCRegion : IDisposable
    {
        private readonly GCKeeper _gcKeeper;
        private readonly FailCause _failCause;
        private readonly long? _size;
        private readonly ILogger _logger;
        private readonly bool _pausedGCScheduler;

        internal NoGCRegion(GCKeeper gcKeeper, FailCause failCause, long? size, bool pausedGCScheduler, ILogger logger)
        {
            _gcKeeper = gcKeeper;
            _failCause = failCause;
            _size = size;
            _pausedGCScheduler = pausedGCScheduler;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_pausedGCScheduler)
            {
                GCScheduler.MarkGCResumed();
            }
            _gcKeeper.OnPayloadDone();
            if (_failCause != FailCause.None && _logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_size} bytes with cause {_failCause.FastToString()}");
        }
    }

    private void ScheduleGC()
    {
        if (_gcScheduleTask.IsCompleted)
        {
            lock (_lock)
            {
                long timeStamp = Environment.TickCount64;
                if (TimeSpan.FromMilliseconds(timeStamp - _lastGcTimeMs).TotalSeconds <= 3)
                {
                    return;
                }

                _lastGcTimeMs = timeStamp;

                if (_gcScheduleTask.IsCompleted)
                {
                    _gcScheduleTask = ScheduleGCInternal();
                }
            }
        }
    }

    private async Task ScheduleGCInternal()
    {
        (GcLevel generation, GcCompaction compacting) = _gcStrategy.GetForcedGCParams();
        if (generation > GcLevel.NoGC)
        {
            // This should give time to finalize response in Engine API
            // Normally we should get block every 12s (5s on some chains)
            // Lets say we process block in 2s, then delay 125ms, then invoke GC
            int postBlockDelayMs = _postBlockDelayMs;
            if (postBlockDelayMs <= 0)
            {
                // Always async
                await Task.Yield();
            }
            else
            {
                if (!await TaskExtensions.DelaySafe(postBlockDelayMs, _shutdownCts?.Token ?? CancellationToken.None)) return;
            }

            if (!_runtime.IsInNoGCRegion)
            {
                ulong forcedGcCount = Interlocked.Increment(ref _forcedGcCount);
                int collectionsPerDecommit = _gcStrategy.CollectionsPerDecommit;

                GCCollectionMode mode = GCCollectionMode.Forced;
                if (collectionsPerDecommit == 0 || (forcedGcCount % (ulong)collectionsPerDecommit == 0))
                {
                    // Also decommit memory back to O/S
                    mode = GCCollectionMode.Aggressive;
                    generation = GcLevel.Gen2;
                    compacting = GcCompaction.Full;
                }

                if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {generation}, compacting {compacting}");
                if (generation == GcLevel.Gen2 && compacting == GcCompaction.Full)
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                }

                GCScheduler.Instance.GCCollect((int)generation, mode, blocking: true, compacting: compacting > 0);
            }
        }
    }
}
