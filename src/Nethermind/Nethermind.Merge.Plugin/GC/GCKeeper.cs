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

public class GCKeeper(IGCStrategy gcStrategy, ILogManager logManager) : IDisposable
{
    private static ulong _forcedGcCount = 0;
    private readonly Lock _lock = new();
    private readonly IGCStrategy _gcStrategy = gcStrategy;
    private readonly int _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
    private readonly ILogger _logger = logManager.GetClassLogger<GCKeeper>();
    private static readonly long _defaultSize = 512.MB;
    private Task _gcScheduleTask = Task.CompletedTask;
    private CancellationTokenSource? _shutdownCts = new();
    private long _lastPayloadTick;
    private long _lastPayloadIntervalMs;

    /// <summary>Minimum observed interval between consecutive payloads, in milliseconds, for the
    /// decommit collection to be considered safe to run; below it payloads are treated as streaming.</summary>
    internal const long MinPayloadIntervalForDecommitMs = 4000;

    public void Dispose() => CancellationTokenExtensions.CancelDisposeAndClear(ref _shutdownCts);

    public IDisposable TryStartNoGCRegion()
    {
        long size = _defaultSize;
        long timestamp = Environment.TickCount64;
        long previousTick = Interlocked.Exchange(ref _lastPayloadTick, timestamp);
        if (previousTick != 0)
        {
            // Clamp to 1 so that a same-tick arrival is distinguishable from "no interval observed".
            Volatile.Write(ref _lastPayloadIntervalMs, Math.Max(1, timestamp - previousTick));
        }

        bool pausedGCScheduler = GCScheduler.MarkGCPaused();
        if (!pausedGCScheduler)
        {
            // A forced collection is in flight (or another NoGC region is active): attempting to
            // start a NoGC region now would block until it finishes — up to the full stop-the-world
            // pause of a decommit. Run this payload without one instead.
            return new NoGCRegion(this, FailCause.GCInProgress, size, pausedGCScheduler, _logger);
        }

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
        GCInProgress,
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
            else if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_size} bytes with cause {_failCause.FastToString()}");
        }
    }

    private bool ShouldDeferDecommit() => ShouldDeferDecommit(Volatile.Read(ref _lastPayloadIntervalMs));

    internal static bool ShouldDeferDecommit(long lastPayloadIntervalMs) =>
        lastPayloadIntervalMs is > 0 and < MinPayloadIntervalForDecommitMs;

    internal long LastPayloadIntervalMs => Volatile.Read(ref _lastPayloadIntervalMs);

    private static long _lastGcTimeMs;

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

            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                ulong forcedGcCount = Interlocked.Increment(ref _forcedGcCount);
                int collectionsPerDecommit = _gcStrategy.CollectionsPerDecommit;

                GCCollectionMode mode = GCCollectionMode.Forced;
                if (collectionsPerDecommit == 0 || (forcedGcCount % (ulong)collectionsPerDecommit == 0))
                {
                    // The decommit is an aggressive gen2 + LOH-compact collection whose
                    // stop-the-world pause can exceed a second on large heaps. At slot cadence the
                    // post-block delay leaves ample idle time, but when payloads stream
                    // back-to-back (catch-up) the next payload lands mid-collection and stalls for
                    // the remainder of the pause — defer until the cadence shows idle gaps.
                    if (ShouldDeferDecommit())
                    {
                        // Retry the decommit on the next scheduled collection.
                        Interlocked.Decrement(ref _forcedGcCount);
                    }
                    else
                    {
                        // Also decommit memory back to O/S
                        mode = GCCollectionMode.Aggressive;
                        generation = GcLevel.Gen2;
                        compacting = GcCompaction.Full;
                    }
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
