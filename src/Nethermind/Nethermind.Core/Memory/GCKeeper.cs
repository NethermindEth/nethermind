// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Core.Memory;

using Nethermind.Core.Extensions;

public class GCKeeper : IDisposable
{
    private long _payloadsSinceDecommit;
    private readonly Lock _lock = new();
    private readonly IGCStrategy _gcStrategy;
    private readonly IGcRegionRuntime _runtime;
    private readonly Action<IThreadPoolWorkItem> _queue;
    private NoGCRegion? _region;
    private readonly int _postBlockDelayMs;
    private readonly ILogger _logger;
    // The runtime splits totalSize as soh = total - loh; when lohSize is omitted it budgets the
    // full totalSize for LOH as well, committing that much LOH inside the per-call EE suspension.
    private static readonly long _lohSize = 64.MB;
    private static readonly long _defaultSize = 512.MB + _lohSize;
    private Task _gcScheduleTask = Task.CompletedTask;
    private CancellationTokenSource? _pendingGcCts;
    private bool _disposed;

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
        : this(gcStrategy, logManager, GcRegionRuntime.Instance) { }

    internal GCKeeper(IGCStrategy gcStrategy, ILogManager logManager, IGcRegionRuntime runtime,
        Action<IThreadPoolWorkItem>? queue = null)
    {
        _gcStrategy = gcStrategy;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _logger = logManager.GetClassLogger<GCKeeper>();
        _runtime = runtime;
        // One outstanding entry bounds pool usage without a dedicated thread for each keeper.
        _queue = queue ?? (static item => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false));
    }

    public void Dispose()
    {
        NoGCRegion? region;
        lock (_lock)
        {
            _disposed = true;
            _pendingGcCts?.Cancel();
            region = _region;
        }
        region?.Dispose();
    }

    /// <summary>Cancels the delayed collection if it has not yet been claimed for execution.</summary>
    public void CancelPendingGC()
    {
        lock (_lock)
        {
            _pendingGcCts?.Cancel();
        }
    }

    /// <summary>Queues no-GC-region entry without waiting for the runtime; disposing the lease ends its protection.</summary>
    public IDisposable TryStartNoGCRegion()
    {
        bool eligible = _gcStrategy.CanStartNoGCRegion();
        NoGCRegion region = new(this, GCScheduler.MarkGCPaused(), eligible);
        lock (_lock)
        {
            if (_disposed) return region;
            if (!eligible)
            {
                if (_logger.IsDebug) _logger.Debug("No-GC region entry disallowed by strategy.");
                return region;
            }
            Interlocked.Increment(ref _payloadsSinceDecommit);
            if (_region is not null)
            {
                if (_logger.IsDebug) _logger.Debug("No-GC region entry skipped: previous entry or region is still active.");
                return region;
            }
            _region = region;
        }

        try
        {
            _queue(region);
        }
        catch
        {
            region.Dispose();
            throw;
        }
        return region;
    }

    private void ReleaseRegion(NoGCRegion region)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_region, region)) _region = null;
        }
    }

    private sealed class NoGCRegion(GCKeeper keeper, bool pausedGCScheduler, bool scheduleGC) : IDisposable, IThreadPoolWorkItem
    {
        private readonly Lock _stateLock = new();
        private bool _released;
        private bool _starting;
        private bool _active;

        public void Execute()
        {
            lock (_stateLock)
            {
                if (_released) return;
                _starting = true;
            }

            bool started = false;
            try
            {
                started = keeper._runtime.TryStart(_defaultSize, _lohSize);
                if (!started && keeper._logger.IsDebug) keeper._logger.Debug("Runtime declined no-GC region entry.");
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
            {
                if (keeper._logger.IsDebug) keeper._logger.Debug($"No-GC region entry failed: {e.Message}");
            }
            catch (Exception e)
            {
                if (keeper._logger.IsError) keeper._logger.Error("No-GC region entry failed.", e);
            }

            lock (_stateLock)
            {
                _starting = false;
                if (started && !_released)
                {
                    _active = true;
                    return;
                }
            }

            if (started) EndRegion();
            else keeper.ReleaseRegion(this);
        }

        public void Dispose()
        {
            bool end;
            bool release;
            lock (_stateLock)
            {
                if (_released) return;
                _released = true;
                end = _active;
                _active = false;
                release = !_starting;
            }

            if (pausedGCScheduler) GCScheduler.MarkGCResumed();
            if (end) EndRegion();
            else if (release) keeper.ReleaseRegion(this);
            if (scheduleGC) keeper.ScheduleGC();
        }

        private void EndRegion()
        {
            try
            {
                if (keeper._runtime.IsActive)
                {
                    keeper._runtime.End();
                }
            }
            catch (InvalidOperationException e)
            {
                if (keeper._logger.IsDebug) keeper._logger.Debug($"No-GC region already ended: {e.Message}");
            }
            catch (Exception e)
            {
                if (keeper._logger.IsError) keeper._logger.Error("No-GC region cleanup failed.", e);
            }
            finally
            {
                keeper.ReleaseRegion(this);
            }
        }
    }

    private long _lastGcTimeMs;

    private void ScheduleGC()
    {
        if (_gcScheduleTask.IsCompleted)
        {
            lock (_lock)
            {
                if (_disposed) return;
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

    internal async Task ScheduleGCInternal()
    {
        (GcLevel generation, GcCompaction compacting) = _gcStrategy.GetForcedGCParams();
        if (generation > GcLevel.NoGC)
        {
            CancellationTokenSource pendingGcCts;
            lock (_lock)
            {
                if (_disposed) return;
                _pendingGcCts = pendingGcCts = new();
            }

            try
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
                    if (!await TaskExtensions.DelaySafe(postBlockDelayMs, pendingGcCts.Token)) return;
                }

                // Claim the collection under the gate, then release it before any runtime work can block.
                lock (_lock)
                {
                    if (pendingGcCts.IsCancellationRequested) return;
                    if (ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
                }

                if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                {
                    long payloadsSinceDecommit = Interlocked.Read(ref _payloadsSinceDecommit);
                    int collectionsPerDecommit = _gcStrategy.CollectionsPerDecommit;

                    GCCollectionMode mode = GCCollectionMode.Forced;
                    bool decommit = collectionsPerDecommit >= 0 && payloadsSinceDecommit >= collectionsPerDecommit;
                    if (decommit)
                    {
                        // Also decommit memory back to O/S
                        mode = GCCollectionMode.Aggressive;
                        generation = GcLevel.Gen2;
                        compacting = GcCompaction.Full;
                    }

                    if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {generation}, compacting {compacting}");
                    bool collected = _runtime.Collect(generation, mode, compacting);
                    if (collected && decommit)
                    {
                        Interlocked.Add(ref _payloadsSinceDecommit, -payloadsSinceDecommit);
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
                    pendingGcCts.Dispose();
                }
            }
        }
    }
}
