// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Globalization;
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

    public void Dispose() => CancellationTokenExtensions.CancelDisposeAndClear(ref _shutdownCts);

    public Task<IDisposable> TryStartNoGCRegionAsync(long blockNumber = -1) => Task.Run(() => TryStartNoGCRegion(blockNumber));

    /// <summary>
    /// Point-in-time GC counters captured at NoGC-region start; deltas are logged at region end for per-block GC diagnostics.
    /// </summary>
    private readonly record struct GCDiagSnapshot(long AllocatedBytes, int Gen0, int Gen1, int Gen2, TimeSpan PauseDuration)
    {
        public static GCDiagSnapshot Capture() => new(
            System.GC.GetTotalAllocatedBytes(precise: false),
            System.GC.CollectionCount(0),
            System.GC.CollectionCount(1),
            System.GC.CollectionCount(2),
            System.GC.GetTotalPauseDuration());
    }

    private IDisposable TryStartNoGCRegion(long blockNumber)
    {
        GCDiagSnapshot diagStart = GCDiagSnapshot.Capture();
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

            return new NoGCRegion(this, failCause, size, pausedGCScheduler, blockNumber, diagStart, _logger);
        }

        return new NoGCRegion(this, FailCause.StrategyDisallowed, size, pausedGCScheduler, blockNumber, diagStart, _logger);
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
        private readonly long _blockNumber;
        private readonly GCDiagSnapshot _diagStart;

        internal NoGCRegion(GCKeeper gcKeeper, FailCause failCause, long? size, bool pausedGCScheduler, long blockNumber, GCDiagSnapshot diagStart, ILogger logger)
        {
            _gcKeeper = gcKeeper;
            _failCause = failCause;
            _size = size;
            _pausedGCScheduler = pausedGCScheduler;
            _blockNumber = blockNumber;
            _diagStart = diagStart;
            _logger = logger;
        }

        /// <summary>
        /// Logs per-block GC activity (alloc bytes, collections by generation, total STW pause) and whether the NoGC region held.
        /// </summary>
        /// <remarks>
        /// Must run before <see cref="System.GC.EndNoGCRegion"/> so that region-exit collections are not attributed to the block,
        /// and so <see cref="GCSettings.LatencyMode"/> still reflects whether the region survived the whole block.
        /// </remarks>
        private void LogDiag()
        {
            if (!_logger.IsInfo) return;

            GCDiagSnapshot end = GCDiagSnapshot.Capture();
            string region = _failCause == FailCause.None
                ? (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion ? "Held" : "Blown")
                : $"NotStarted-{_failCause.FastToString()}";
            double allocMB = (end.AllocatedBytes - _diagStart.AllocatedBytes) / 1_000_000.0;
            double pauseMs = (end.PauseDuration - _diagStart.PauseDuration).TotalMilliseconds;
            _logger.Info(string.Create(CultureInfo.InvariantCulture,
                $"[GCDIAG] block={_blockNumber} region={region} allocMB={allocMB:F1} gen0={end.Gen0 - _diagStart.Gen0} gen1={end.Gen1 - _diagStart.Gen1} gen2={end.Gen2 - _diagStart.Gen2} pauseMs={pauseMs:F2}"));
        }

        public void Dispose()
        {
            LogDiag();
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

                long startTimestamp = Stopwatch.GetTimestamp();
                bool performed = GCScheduler.Instance.GCCollect((int)generation, mode, blocking: true, compacting: compacting > 0);
                if (_logger.IsInfo) _logger.Info(string.Create(CultureInfo.InvariantCulture,
                    $"[GCDIAG] forcedGC gen={(int)generation} mode={mode} performed={performed} ms={Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F1}"));
            }
            else if (_logger.IsInfo)
            {
                _logger.Info("[GCDIAG] forcedGC skipped reason=InNoGCRegion");
            }
        }
    }
}
