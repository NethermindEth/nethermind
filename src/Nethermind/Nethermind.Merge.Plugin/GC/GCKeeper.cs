// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
    private readonly IGCRuntime _runtime;
    private readonly int _postBlockDelayMs;
    private readonly ILogger _logger;
    // The runtime splits totalSize as soh = total - loh; when lohSize is omitted it budgets the
    // full totalSize for LOH as well, committing that much LOH inside the per-call EE suspension.
    private static readonly long _lohSize = 64.MB;
    private static readonly long _defaultSize = 512.MB + _lohSize;
    // Long enough for the response and the fork-choice update that follows a payload to be handled first; the
    // scheduler refuses the sweep outright if an engine call is still in flight when the window ends.
    private const int PostBlockSettleMs = 100;
    private Task _gcScheduleTask = Task.CompletedTask;
    private CancellationTokenSource? _shutdownCts = new();
    private readonly CancellationToken _shutdown;
    private long _lastGcTimeMs;
    private bool _disposed;
    private bool _keeperStopped;

    // Starting a no-GC region makes the runtime wait for any background GC in flight, which on a large heap
    // takes seconds. The engine request therefore never calls into the runtime itself: this thread does,
    // and a request that finishes first simply runs without a region.
    private readonly ConcurrentQueue<RegionRequest> _requests = new();
    private readonly SemaphoreSlim _requestSignal = new(0);
    // Shared by every request: a lease may be released after shutdown, so this is never disposed.
    private readonly ManualResetEventSlim _releaseSignal = new(false);
    private Thread? _regionThread;

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager) : this(gcStrategy, logManager, GCRuntime.Instance)
    {
    }

    internal GCKeeper(IGCStrategy gcStrategy, ILogManager logManager, IGCRuntime runtime)
    {
        _gcStrategy = gcStrategy;
        _runtime = runtime;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _logger = logManager.GetClassLogger<GCKeeper>();
        _shutdown = _shutdownCts!.Token;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            CancellationTokenExtensions.CancelDisposeAndClear(ref _shutdownCts);
            // A running keeper thread owns the signal until it has drained the queue.
            if (_regionThread is null) _requestSignal.Dispose();
        }
    }

    /// <summary>Asks for a no-GC region around the current engine request; disposing the result releases it.</summary>
    /// <remarks>
    /// Returns without touching the runtime. The region becomes active once the keeper thread has entered it,
    /// which is immediate unless a background GC is running, in which case the request proceeds without one
    /// rather than waiting for that GC to finish. Forced collections are excluded for the lifetime of the lease.
    /// </remarks>
    public IDisposable TryStartNoGCRegion()
    {
        bool pausedGCScheduler = GCScheduler.MarkGCPaused();
        if (!_gcStrategy.CanStartNoGCRegion())
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_defaultSize} bytes with cause {FailCause.StrategyDisallowed.FastToString()}");
            return new NoGCRegion(null, pausedGCScheduler);
        }

        lock (_lock)
        {
            if (_disposed || _keeperStopped) return new NoGCRegion(null, pausedGCScheduler);

            // This payload's own arrival number: any later one is the next payload arriving.
            RegionRequest request = new(_releaseSignal, GCScheduler.ClaimLatencySensitiveRequest());
            _requests.Enqueue(request);
            EnsureKeeperRunning();
            _requestSignal.Release();
            return new NoGCRegion(request, pausedGCScheduler);
        }
    }

    private void EnsureKeeperRunning()
    {
        if (_regionThread is not null) return;

        _regionThread = new Thread(RunRegions) { IsBackground = true, Name = "GC region keeper" };
        _regionThread.Start();
    }

    private void RunRegions()
    {
        bool sweepPending = false;
        long requestsSeenBeforeBlock = 0;
        try
        {
            while (true)
            {
                if (sweepPending)
                {
                    if (SettleAfterBlock(requestsSeenBeforeBlock)) SweepAfterBlock();
                    sweepPending = false;
                }
                else
                {
                    _requestSignal.Wait(_shutdown);
                }

                while (_requests.TryDequeue(out RegionRequest? request))
                {
                    KeepRegion(request);
                    sweepPending = true;
                    requestsSeenBeforeBlock = request.RequestsSeen;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // An unhandled exception here would take the process down; requests keep running without regions.
            if (_logger.IsError) _logger.Error("GC region keeper failed.", e);
        }
        finally
        {
            LeaveRegionsBehind();
        }
    }

    /// <summary>Holds the region for one request until its lease is released.</summary>
    private void KeepRegion(RegionRequest request)
    {
        // Entering a region can block on the runtime, so a shutdown that arrived mid-drain stops here.
        _shutdown.ThrowIfCancellationRequested();
        if (request.IsReleased) return;

        try
        {
            FailCause failCause = StartRegion();
            if (failCause != FailCause.None)
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to start NoGCRegion with {_defaultSize} bytes with cause {failCause.FastToString()}");
                // The block still runs; sweeping while it does would defeat the purpose.
                WaitForRelease(request);
                return;
            }

            // Also when the runtime kept us waiting above until the block was done: the region then ends at once.
            WaitForRelease(request);
            EndRegion(request.RequestsSeen);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("GC region keeper failed for a request.", e);
        }
    }

    private void WaitForRelease(RegionRequest request)
    {
        // The flag is checked before every wait, so a Set consumed on behalf of another request is never lost.
        while (!request.IsReleased)
        {
            _releaseSignal.Wait(_shutdown);
            _releaseSignal.Reset();
        }
    }

    /// <summary>
    /// Collects the block's garbage between requests instead of letting the runtime do it on the next payload's
    /// first allocations; returns false when the next payload is already arriving.
    /// </summary>
    private bool SettleAfterBlock(long requestsSeenBeforeBlock)
    {
        long deadline = Environment.TickCount64 + PostBlockSettleMs;
        for (long remaining = PostBlockSettleMs; remaining > 0; remaining = deadline - Environment.TickCount64)
        {
            // A permit left over from several requests drained in one pass is not a new request.
            if (_requestSignal.Wait((int)remaining, _shutdown) && !_requests.IsEmpty) return false;
        }

        return _requests.IsEmpty && GCScheduler.LatencySensitiveRequests == requestsSeenBeforeBlock;
    }

    private void LeaveRegionsBehind()
    {
        try
        {
            if (_runtime.InNoGCRegion) EndRegion(GCScheduler.LatencySensitiveRequests);
        }
        finally
        {
            lock (_lock)
            {
                _keeperStopped = true;
                _requests.Clear();
                _requestSignal.Dispose();
            }
        }
    }

    private FailCause StartRegion()
    {
        try
        {
            return _runtime.TryStartNoGCRegion(_defaultSize, _lohSize)
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

    private void EndRegion(long requestsSeen)
    {
        if (!_runtime.InNoGCRegion)
        {
            // The runtime exits the region itself when the allocation budget is exhausted mid-block.
            if (_logger.IsDebug) _logger.Debug($"Failed to keep in NoGCRegion with {_defaultSize} bytes");
            return;
        }

        try
        {
            _runtime.EndNoGCRegion();
            ScheduleGC(requestsSeen);
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
        _runtime.Collect(sweepGeneration, GCCollectionMode.Forced, compacting: false, trimNativeMemory: false);
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

    /// <param name="requestsSeen">This payload's arrival number; a higher count later means the next one is arriving.</param>
    private sealed class RegionRequest(ManualResetEventSlim releaseSignal, long requestsSeen)
    {
        private volatile bool _released;

        public long RequestsSeen => requestsSeen;

        public bool IsReleased => _released;

        public void Release()
        {
            _released = true;
            releaseSignal.Set();
        }
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

    /// <summary>The collection scheduled after the last block, for tests to await its decision.</summary>
    internal Task PendingCollection
    {
        get
        {
            lock (_lock) return _gcScheduleTask;
        }
    }

    private void ScheduleGC(long requestsSeen)
    {
        if (_shutdown.IsCancellationRequested) return;

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
                    _gcScheduleTask = ScheduleGCInternal(requestsSeen);
                }
            }
        }
    }

    private async Task ScheduleGCInternal(long requestsSeen)
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
                if (!await TaskExtensions.DelaySafe(postBlockDelayMs, _shutdown)) return;
            }

            // The next payload started arriving since this block began: the engine is busy, so leave collection
            // to the per-block sweep rather than pausing a request that is being parsed or already in flight.
            if (GCScheduler.LatencySensitiveRequests != requestsSeen) return;

            if (!_runtime.InNoGCRegion)
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
                    _runtime.CompactLargeObjectHeapOnce();
                }

                _runtime.Collect((int)generation, mode, compacting: compacting > 0, trimNativeMemory: true);
            }
        }
    }
}
