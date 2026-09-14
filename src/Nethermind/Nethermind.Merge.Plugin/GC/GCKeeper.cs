// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
    private readonly Lock _lock = new();
    private readonly IGCStrategy _gcStrategy;
    private readonly int _postBlockDelayMs;
    private readonly ILogger _logger;
    // The runtime splits totalSize as soh = total - loh; when lohSize is omitted it budgets the
    // full totalSize for LOH as well, committing that much LOH inside the per-call EE suspension.
    private static readonly long _lohSize = 64.MB;
    private static readonly long _defaultSize = 512.MB + _lohSize;
    // Long enough for the response and the fork-choice update that follows a payload to go out first.
    private const int PostBlockSettleMs = 20;
    private Task _gcScheduleTask = Task.CompletedTask;
    private CancellationTokenSource? _shutdownCts = new();
    private long _regionRequests;

    // Starting a no-GC region makes the runtime wait for any background GC in flight, which on a large heap
    // takes seconds. The engine request therefore never calls into the runtime itself: this thread does,
    // and a request that finishes first simply runs without a region.
    private readonly ConcurrentQueue<RegionRequest> _requests = new();
    private readonly AutoResetEvent _requestSignal = new(false);
    private readonly Thread _regionThread;

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
    {
        _gcStrategy = gcStrategy;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _logger = logManager.GetClassLogger<GCKeeper>();
        CancellationToken shutdown = _shutdownCts!.Token;
        _regionThread = new Thread(() => RunRegions(shutdown)) { IsBackground = true, Name = "GC region keeper" };
        _regionThread.Start();
    }

    public void Dispose()
    {
        CancellationTokenExtensions.CancelDisposeAndClear(ref _shutdownCts);
        _requestSignal.Set();
    }

    /// <summary>Asks for a no-GC region around the current engine request; disposing the result releases it.</summary>
    /// <remarks>
    /// Returns without touching the runtime. The region becomes active once the keeper thread has entered it,
    /// which is immediate unless a background GC is running, in which case the request proceeds without one
    /// rather than waiting for that GC to finish.
    /// </remarks>
    public IDisposable TryStartNoGCRegion()
    {
        bool pausedGCScheduler = GCScheduler.MarkGCPaused();
        if (!_gcStrategy.CanStartNoGCRegion())
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_defaultSize} bytes with cause {FailCause.StrategyDisallowed.FastToString()}");
            return new NoGCRegion(null, pausedGCScheduler);
        }

        Interlocked.Increment(ref _regionRequests);
        RegionRequest request = new();
        _requests.Enqueue(request);
        _requestSignal.Set();
        return new NoGCRegion(request, pausedGCScheduler);
    }

    private void RunRegions(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _requestSignal.WaitOne();
            while (!token.IsCancellationRequested && _requests.TryDequeue(out RegionRequest? request))
            {
                try
                {
                    KeepRegion(request);
                }
                catch (Exception e)
                {
                    // An unhandled exception here would take the process down; the request itself is unaffected.
                    if (_logger.IsError) _logger.Error("GC region keeper failed.", e);
                }
            }
        }
    }

    private void KeepRegion(RegionRequest request)
    {
        if (request.IsReleased) return;

        FailCause failCause = StartRegion();
        if (failCause != FailCause.None)
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_defaultSize} bytes with cause {failCause.FastToString()}");
            return;
        }

        // The region is process-wide: a request released while we were blocked above gets it ended at once.
        request.WaitForRelease();
        EndRegion();

        // Collect the block's garbage now, between requests, instead of letting the runtime do it on the
        // next payload's first allocations. A request arriving inside the settle window takes precedence.
        if (!_requestSignal.WaitOne(PostBlockSettleMs))
        {
            SweepAfterBlock();
        }
    }

    private FailCause StartRegion()
    {
        try
        {
            return System.GC.TryStartNoGCRegion(_defaultSize, _lohSize, disallowFullBlockingGC: true)
                ? FailCause.None
                : FailCause.GCFailedToStartNoGCRegion;
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

    private void EndRegion()
    {
        if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
        {
            // The runtime exits the region itself when the allocation budget is exhausted mid-block.
            if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with {_defaultSize} bytes");
            return;
        }

        try
        {
            System.GC.EndNoGCRegion();
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

    private void SweepAfterBlock()
    {
        (GcLevel generation, _) = _gcStrategy.GetForcedGCParams();
        if (generation == GcLevel.NoGC) return;

        // Non-compacting and at most gen1: cheap enough to run after every block; compaction stays with the
        // idle-time collection below.
        int sweepGeneration = Math.Min((int)generation, (int)GcLevel.Gen1);
        GCScheduler.Instance.GCCollect(sweepGeneration, GCCollectionMode.Forced, blocking: true, compacting: false, trimNativeMemory: false);
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

    private sealed class RegionRequest
    {
        private readonly ManualResetEventSlim _released = new(false);

        public bool IsReleased => _released.IsSet;

        public void Release() => _released.Set();

        public void WaitForRelease() => _released.Wait();
    }

    private sealed class NoGCRegion(RegionRequest? request, bool pausedGCScheduler) : IDisposable
    {
        public void Dispose()
        {
            if (pausedGCScheduler)
            {
                GCScheduler.MarkGCResumed();
            }
            request?.Release();
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
            long requestsAtSchedule = Volatile.Read(ref _regionRequests);

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

            // Another payload arrived during the delay: the engine is busy, so leave collection to the per-block
            // sweep rather than pausing a request that may be in flight or about to arrive.
            if (Volatile.Read(ref _regionRequests) != requestsAtSchedule) return;

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

                GCScheduler.Instance.GCCollect((int)generation, mode, blocking: true, compacting: compacting > 0);
            }
        }
    }
}
