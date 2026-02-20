// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.GC;

public class GCKeeper
{
    private static ulong _forcedGcCount = 0;
    private readonly Lock _lock = new();
    private readonly IGCStrategy _gcStrategy;
    private readonly ILogger _logger;
    private static readonly long _defaultSize = 512.MB();
    private Task _gcScheduleTask = Task.CompletedTask;
    private CancellationTokenSource? _pendingGcCts;

    // Block arrival tracking for adaptive GC scheduling
    private long _lastBlockEndTick;
    private double _observedGapMs = -1; // EMA of inter-block gaps; -1 = no data yet
    private long _lastGen2Tick;

    // Adaptive scheduling constants
    private const double GapEmaAlpha = 0.3;
    private const int MinGapForForcedGcMs = 200;
    private const int MinGapForGen2Ms = 1000;
    private const int MinGapForAggressiveMs = 3000;
    private const int Gen2LivenessLimitMs = 5 * 60 * 1000; // 5 minutes
    private const int ResponseFinalizeDelayMs = 20;

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
    {
        _gcStrategy = gcStrategy;
        _logger = logManager.GetClassLogger<GCKeeper>();
        _lastGen2Tick = Environment.TickCount64;
    }

    public IDisposable StartNoGCRegion()
    {
        long now = Environment.TickCount64;

        // Update inter-block gap estimate from observed arrival rate
        long lastEnd = Volatile.Read(ref _lastBlockEndTick);
        if (lastEnd > 0)
        {
            double gapMs = now - lastEnd;
            double current = _observedGapMs;
            _observedGapMs = current < 0
                ? gapMs
                : GapEmaAlpha * gapMs + (1 - GapEmaAlpha) * current;
        }

        // Cancel any pending post-block GC — incoming block takes priority
        Interlocked.Exchange(ref _pendingGcCts, null)?.Cancel();

        long size = _defaultSize;
        bool pausedGCScheduler = GCScheduler.MarkGCPaused();
        if (_gcStrategy.CanStartNoGCRegion())
        {
            FailCause failCause = FailCause.None;
            try
            {
                if (!System.GC.TryStartNoGCRegion(size, disallowFullBlockingGC: true))
                {
                    failCause = FailCause.GCFailedToStartNoGCRegion;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                failCause = FailCause.TotalSizeExceededTheEphemeralSegmentSize;
            }
            catch (InvalidOperationException)
            {
                failCause = FailCause.AlreadyInNoGCRegion;
            }
            catch (Exception e)
            {
                failCause = FailCause.Exception;

                if (_logger.IsError) _logger.Error($"{nameof(System.GC.TryStartNoGCRegion)} failed with exception.", e);
            }

            return new NoGCRegion(this, failCause, size, pausedGCScheduler, _logger);
        }

        return new NoGCRegion(this, FailCause.StrategyDisallowed, size, pausedGCScheduler, _logger);
    }

    private enum FailCause
    {
        None,
        StrategyDisallowed,
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
            // Always record block end time for adaptive gap tracking
            Volatile.Write(ref _gcKeeper._lastBlockEndTick, Environment.TickCount64);

            if (_pausedGCScheduler)
            {
                GCScheduler.MarkGCResumed();
            }
            if (_failCause == FailCause.None)
            {
                if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                {
                    try
                    {
                        System.GC.EndNoGCRegion();
                        _gcKeeper.ScheduleGC();
                    }
                    catch (InvalidOperationException)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with Exception with {_size} bytes");
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error($"{nameof(System.GC.EndNoGCRegion)} failed with exception.", e);
                    }
                }
                else if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with {_size} bytes");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_size} bytes with cause {_failCause.FastToString()}");
                // Still schedule GC even when NoGCRegion was not started
                _gcKeeper.ScheduleGC();
            }
        }
    }

    private void ScheduleGC()
    {
        if (_gcScheduleTask.IsCompleted)
        {
            lock (_lock)
            {
                if (_gcScheduleTask.IsCompleted)
                {
                    _gcScheduleTask = ScheduleGCInternal();
                }
            }
        }
    }

    private async Task ScheduleGCInternal()
    {
        (GcLevel maxGeneration, GcCompaction maxCompacting) = _gcStrategy.GetForcedGCParams();
        if (maxGeneration <= GcLevel.NoGC)
            return;

        double gap = _observedGapMs;
        long now = Environment.TickCount64;
        long timeSinceGen2 = now - Volatile.Read(ref _lastGen2Tick);
        bool gen2Overdue = timeSinceGen2 > Gen2LivenessLimitMs;

        // Determine collection level based on observed inter-block gap
        GcLevel generation;
        GcCompaction compacting;
        int delayMs;
        GCCollectionMode mode;

        if (gap >= 0 && gap < MinGapForForcedGcMs && !gen2Overdue)
        {
            // Rapid blocks (Arbitrum ~250ms, benchmarks back-to-back) — let background GC handle it
            if (_logger.IsDebug) _logger.Debug($"Skipping forced GC: observed gap {gap:F0}ms too short");
            return;
        }

        if (gen2Overdue)
        {
            // Safety net: force Gen2 to prevent memory leaks
            generation = GcLevel.Gen2;
            compacting = GcCompaction.Full;
            mode = GCCollectionMode.Aggressive;
            delayMs = ResponseFinalizeDelayMs;
            if (_logger.IsDebug) _logger.Debug($"Gen2 overdue ({timeSinceGen2}ms since last), forcing collection");
        }
        else if (gap >= MinGapForAggressiveMs && maxGeneration >= GcLevel.Gen2)
        {
            // Large gap (>3s) — can afford full collection with decommit check
            ulong count = Interlocked.Increment(ref _forcedGcCount);
            int collectionsPerDecommit = _gcStrategy.CollectionsPerDecommit;
            if (collectionsPerDecommit == 0 || (count % (ulong)collectionsPerDecommit == 0))
            {
                generation = GcLevel.Gen2;
                compacting = GcCompaction.Full;
                mode = GCCollectionMode.Aggressive;
            }
            else
            {
                generation = maxGeneration;
                compacting = maxCompacting;
                mode = GCCollectionMode.Forced;
            }
            delayMs = Math.Min(200, (int)(gap / 10));
        }
        else if (gap >= MinGapForGen2Ms && maxGeneration >= GcLevel.Gen2)
        {
            // Medium gap (1-3s) — Gen2 without LOH compact
            generation = GcLevel.Gen2;
            compacting = GcCompaction.Yes;
            mode = GCCollectionMode.Forced;
            delayMs = Math.Min(100, (int)(gap / 10));
            Interlocked.Increment(ref _forcedGcCount);
        }
        else if (gap >= MinGapForForcedGcMs)
        {
            // Moderate gap (200ms-1s) — Gen1 only
            generation = (GcLevel)Math.Min((int)maxGeneration, (int)GcLevel.Gen1);
            compacting = GcCompaction.No;
            mode = GCCollectionMode.Forced;
            delayMs = ResponseFinalizeDelayMs;
            Interlocked.Increment(ref _forcedGcCount);
        }
        else
        {
            // First block or no gap data — use strategy defaults
            generation = maxGeneration;
            compacting = maxCompacting;
            mode = GCCollectionMode.Forced;
            delayMs = _gcStrategy.PostBlockDelayMs > 0 ? _gcStrategy.PostBlockDelayMs : ResponseFinalizeDelayMs;
            Interlocked.Increment(ref _forcedGcCount);
        }

        // Cancellable delay — cancelled when the next block arrives
        CancellationTokenSource cts = new();
        Interlocked.Exchange(ref _pendingGcCts, cts)?.Cancel();

        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, cts.Token);
            else
                await Task.Yield();
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug("Post-block GC cancelled: new block arrived");
            return;
        }

        if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
        {
            if (_logger.IsDebug) _logger.Debug($"Forcing GC gen {generation}, compacting {compacting}, gap {gap:F0}ms");

            if (generation == GcLevel.Gen2 && compacting == GcCompaction.Full)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            if (GCScheduler.Instance.GCCollect((int)generation, mode, blocking: true, compacting: compacting > 0))
            {
                if (generation >= GcLevel.Gen2)
                {
                    Volatile.Write(ref _lastGen2Tick, Environment.TickCount64);
                }
            }
        }
    }
}
