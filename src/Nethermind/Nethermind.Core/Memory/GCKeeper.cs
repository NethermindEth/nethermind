// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Core.Memory;

using Nethermind.Core.Extensions;

public class GCKeeper : IDisposable
{
    private const int DecommitIdleDelayMs = 3_000;
    private const int DefaultMinMsBetweenCollections = 3_000;
    // Payloads that leave less idle time than this come back to back (catch-up, replay): the timing stays as configured.
    private const int MinIdleGapForCadenceMs = 40;
    // The adapted post-block delay leaves the collection three quarters of the idle gap to finish before the next payload.
    private const int IdleGapPerPostBlockDelay = 4;
    private const int MinPostBlockDelayMs = 10;
    private long _payloadsSinceDecommit;
    private readonly Lock _lock = new();
    private readonly IGCStrategy _gcStrategy;
    private readonly IGcRegionRuntime _runtime;
    private readonly Action<IThreadPoolWorkItem> _queue;
    private readonly Func<int, CancellationToken, Task<bool>> _delay;
    private NoGCRegion? _region;
    private readonly int _postBlockDelayMs;
    private readonly bool _adaptPostBlockDelay;
    private readonly bool _entersRegion;
    private readonly TimeProvider _time;
    private readonly long _ticksPerMs;
    private readonly PayloadCadence _cadence = new();
    private int _payloadsInProcessing;
    private readonly ILogger _logger;
    // The runtime splits totalSize as soh = total - loh; when lohSize is omitted it budgets the
    // full totalSize for LOH as well, committing that much LOH inside the per-call EE suspension.
    private static readonly long _lohSize = 64.MB;
    private static readonly long _defaultSize = 512.MB + _lohSize;
    private Task _gcScheduleTask = Task.CompletedTask;
    private CancellationTokenSource? _pendingGcCts;
    private bool _disposed;
    private int _pendingEntries;
    private TaskCompletionSource? _entriesDrained;
    // Held across a payload's region entry, and across the re-commit's entry and end, so the two never overlap.
    private readonly Lock _runtimeLock = new();

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
        : this(gcStrategy, logManager, GcRegionRuntime.Instance) { }

    internal GCKeeper(IGCStrategy gcStrategy, ILogManager logManager, IGcRegionRuntime runtime,
        Action<IThreadPoolWorkItem>? queue = null, Func<int, CancellationToken, Task<bool>>? delay = null, TimeProvider? time = null)
    {
        _gcStrategy = gcStrategy;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _adaptPostBlockDelay = gcStrategy.AdaptivePostBlockDelay;
        _entersRegion = gcStrategy.NoGcRegionOnNewPayload == NoGcRegionMode.Always;
        _time = time ?? TimeProvider.System;
        _ticksPerMs = Math.Max(1, _time.TimestampFrequency / 1000);
        _logger = logManager.GetClassLogger<GCKeeper>();
        _runtime = runtime;
        _delay = delay ?? TaskExtensions.DelaySafe;
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
        region?.ForceRelease();
    }

    /// <summary>Stops the keeper and waits for queued region entry and collection to finish.</summary>
    internal async Task StopAsync()
    {
        Dispose();
        Task entries;
        Task collection;
        lock (_lock)
        {
            entries = _pendingEntries == 0 && _region is null ? Task.CompletedTask
                : (_entriesDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            collection = _gcScheduleTask;
        }
        await Task.WhenAll(entries, collection).ConfigureAwait(false);
    }

    private void CompleteEntry()
    {
        lock (_lock)
        {
            if (--_pendingEntries == 0 && _region is null) _entriesDrained?.TrySetResult();
        }
    }

    /// <summary>Cancels the delayed collection if it has not yet been claimed for execution, and the re-commit or decommit still to follow it.</summary>
    public void CancelPendingGC()
    {
        lock (_lock)
        {
            _pendingGcCts?.Cancel();
        }
    }

    /// <summary>Queues no-GC-region entry without waiting for the runtime; disposing the lease ends its protection.</summary>
    /// <remarks>
    /// With <see cref="NoGcRegionMode.Never"/> an eligible payload enters no region but is otherwise handled the same: it
    /// pauses the <see cref="GCScheduler"/> for its lifetime, counts toward the decommit and schedules the post-block collection.
    /// </remarks>
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
            _cadence.OnPayloadStart(NowMs(), idle: _payloadsInProcessing == 0);
            Metrics.GcKeeperMedianIdleGapMs = _cadence.MedianIdleGapMs;
            Metrics.GcKeeperMedianPayloadIntervalMs = _cadence.MedianIntervalMs;
            _payloadsInProcessing++;
            region.InProcessing = true;
            if (!_entersRegion) return region;
            if (_region is not null)
            {
                // A payload that starts while the previous one is still inside its region shares that region rather
                // than running unprotected: it then ends when the last payload leaves, not when the first returns.
                // The newcomer keeps a lease of its own so its scheduler pause and its collection stay its own.
                if (_region.TryAddLease()) return new SharedRegionLease(_region, region);
                if (_logger.IsDebug) _logger.Debug("No-GC region entry skipped: previous entry or region is still active.");
                return region;
            }
            _region = region;
            _pendingEntries++;
        }

        try
        {
            _queue(region);
        }
        catch
        {
            region.Dispose();
            CompleteEntry();
            throw;
        }
        return region;
    }

    /// <summary>Marks an eligible payload done; once none is left in processing, the idle gap before the next one starts.</summary>
    private void PayloadLeft()
    {
        lock (_lock)
        {
            if (--_payloadsInProcessing == 0) _cadence.OnIdle(NowMs());
        }
    }

    private long NowMs() => _time.GetTimestamp() / _ticksPerMs;

    /// <summary>The post-block delay: the strategy's, or a share of the idle gap payloads leave when that is shorter.</summary>
    /// <remarks>
    /// The strategy's delay suits the slot time, but payloads can come faster than their slots (a 200 ms block time on a
    /// chain configured for 2 s), and a payload cancels the collection still waiting for it. Back to back, before there
    /// are enough samples, or when the strategy fixes the delay, its delay stays. Called under <see cref="_lock"/>.
    /// </remarks>
    private int EffectivePostBlockDelayMs()
    {
        long idleGapMs = _cadence.MedianIdleGapMs;
        if (!_adaptPostBlockDelay || idleGapMs < MinIdleGapForCadenceMs) return _postBlockDelayMs;
        return (int)Math.Min(_postBlockDelayMs, Math.Max(MinPostBlockDelayMs, idleGapMs / IdleGapPerPostBlockDelay));
    }

    /// <summary>The minimum spacing between keeper collections: 3 s, or half the payload interval when that is shorter.</summary>
    /// <remarks>
    /// A fixed 3 s let a collection run after every other payload at 2 s blocks and after every fifteenth at 200 ms; half
    /// the interval lets one run after every payload and still keeps two out of one idle gap. Back to back, or before
    /// there are enough samples, the 3 s stay. Called under <see cref="_lock"/>.
    /// </remarks>
    private long EffectiveMinMsBetweenCollections()
    {
        long intervalMs = _cadence.MedianIntervalMs;
        if (_cadence.MedianIdleGapMs < MinIdleGapForCadenceMs || intervalMs < 0) return DefaultMinMsBetweenCollections;
        return Math.Min(DefaultMinMsBetweenCollections, intervalMs / 2);
    }

    private void ReleaseRegion(NoGCRegion region)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_region, region)) _region = null;
            if (_pendingEntries == 0 && _region is null) _entriesDrained?.TrySetResult();
        }
    }

    private sealed class NoGCRegion(GCKeeper keeper, bool pausedGCScheduler, bool scheduleGC) : IDisposable, IThreadPoolWorkItem
    {
        private readonly Lock _stateLock = new();
        private bool _released;
        private bool _starting;
        private const int MaxLeases = 2;
        private bool _active;
        private int _leases = 1;
        private bool _ownerReleased;

        /// <summary>Set on an eligible payload's own lease, which the keeper counts as in processing until it is released.</summary>
        public bool InProcessing;

        /// <summary>Takes a lease for a payload that starts while the payload that admitted this region is inside it.</summary>
        /// <remarks>
        /// Refused once that payload has let go, which bounds the chain at two: the budget is entered once and sized
        /// for one payload, so a chain that kept renewing itself would spread it over arbitrarily many blocks until
        /// the runtime ended the region itself mid-block, and would hold the keeper's slot so no region could ever be
        /// admitted again. A third overlapping payload takes the skipped path instead, as it did before leases.
        /// </remarks>
        /// <returns><c>false</c> when the region is on its way out, or its admitting payload has already released.</returns>
        public bool TryAddLease()
        {
            lock (_stateLock)
            {
                // Counted, not just gated on the owner: the owner's release is deferred past the answer, so it can
                // still be outstanding when a third payload arrives, and the budget is entered once for one payload.
                if (_released || _ownerReleased || _leases >= MaxLeases) return false;
                _leases++;
                return true;
            }
        }

        public void Execute()
        {
            try
            {
                ExecuteEntry();
            }
            finally
            {
                keeper.CompleteEntry();
            }
        }

        private void ExecuteEntry()
        {
            lock (_stateLock)
            {
                if (_released) return;
                _starting = true;
            }

            bool started = false;
            try
            {
                // Waits for a re-commit after a collection to end its region (see RecommitAfterCollection).
                lock (keeper._runtimeLock)
                {
                    started = keeper._runtime.TryStart(_defaultSize, _lohSize);
                }
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

        /// <summary>Released by the payload that admitted the region, after which it takes no new leases.</summary>
        public void Dispose() => Release(owner: true);

        /// <summary>Ends the region whatever is still leased, for the keeper's own shutdown.</summary>
        public void ForceRelease() => Release(force: true);

        public void Release(bool owner = false, bool force = false)
        {
            bool end;
            bool release;
            bool left;
            bool covering;
            lock (_stateLock)
            {
                if (_released) return;
                // The payload is done whether or not the region still covers another one.
                left = owner && !_ownerReleased && InProcessing;
                if (owner) _ownerReleased = true;
                if (force) _leases = 0; else _leases--;
                // Still covering another payload: the region is not this lease's to end.
                covering = _leases > 0;
                end = !covering && _active;
                release = !covering && !_starting;
                if (!covering)
                {
                    _released = true;
                    _active = false;
                }
            }

            // Outside the region's lock: the keeper takes its own lock, which is held around TryAddLease.
            if (left) keeper.PayloadLeft();
            if (covering) return;
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

    /// <summary>One overlapping payload's hold on a region another payload admitted.</summary>
    /// <param name="shared">The region covering this payload, released when the last payload in it is done.</param>
    /// <param name="own">This payload's own region, never admitted, carrying its scheduler pause and its collection.</param>
    private sealed class SharedRegionLease(NoGCRegion shared, NoGCRegion own) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            shared.Release();
            own.Dispose();
        }
    }

    private long? _lastGcTimeMs;

    /// <summary>
    /// Enters and at once ends a no-GC region right after a keeper collection, so the next payload's entry finds its
    /// budget linked and committed rather than linking and committing it inside its own suspension.
    /// </summary>
    /// <remarks>
    /// <para>A collection releases the gen0 regions the last entry linked, and the aggressive one also decommits every
    /// free region, so the entry after either links the whole budget again (after a decommit, committing it too) while
    /// every thread is suspended, on the payload's path; without a collection in between, an entry links only what the
    /// last payload allocated. Ending a region leaves the regions it linked committed until the next collection
    /// (dotnet/runtime v10.0.0, gc.cpp: extend_soh_for_no_gc commits without touching the pages, and decommit only
    /// takes regions a collection released), so the next entry reuses them and RSS does not grow until they are
    /// allocated in. The decommit still returns everything else.</para>
    /// <para>Skipped once the next payload has cancelled the pending collection, while a payload is in processing or a
    /// region is pending or active, when the strategy disallows regions or payloads enter none
    /// (<see cref="NoGcRegionMode.Never"/>), and on shutdown. A payload admitted meanwhile queues its entry, which waits
    /// on <see cref="_runtimeLock"/> until the throwaway region has ended. The pending collection stays attached, so a
    /// decommit still to come keeps its idle wait cancellable.</para>
    /// </remarks>
    private void RecommitAfterCollection(CancellationTokenSource pendingGcCts)
    {
        bool allowed = _entersRegion && _gcStrategy.CanStartNoGCRegion();
        lock (_lock)
        {
            if (!allowed || pendingGcCts.IsCancellationRequested || _disposed || _payloadsInProcessing > 0 || _region is not null || _runtime.IsActive) return;
            // Taken under the keeper's lock, so no entry can be between its admission and its runtime call here.
            _runtimeLock.Enter();
        }

        try
        {
            if (_runtime.TryStart(_defaultSize, _lohSize))
            {
                Interlocked.Increment(ref Metrics.NoGcRegionRecommits);
                if (_runtime.IsActive) _runtime.End();
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            if (_logger.IsDebug) _logger.Debug($"No-GC region re-commit failed: {e.Message}");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("No-GC region re-commit failed.", e);
        }
        finally
        {
            _runtimeLock.Exit();
        }
    }

    private enum DecommitDecision { None, Skip, Pressure, SafetyNet, LoadUnknown }

    /// <summary>A decision on a due decommit and the inputs it was made from, kept for its log line.</summary>
    private readonly record struct DecommitCheck(DecommitDecision Decision, GcMemoryLoad Load, int LoadPercent, int Unconditional, long Payloads);

    private DecommitDecision _lastLoggedDecommitDecision;

    /// <summary>Decides whether a due decommit runs: under memory pressure, or once the unconditional count is reached.</summary>
    /// <remarks>
    /// A skipped decommit stays due, so the load is checked again after every later payload; the count is not reset.
    /// Without a threshold (0 or less) every due decommit runs, the memory load is not read and nothing is logged.
    /// A skip is logged here; a decommit only once its collection has run (<see cref="LogDecommitDecision"/>).
    /// </remarks>
    private DecommitCheck CheckDecommit(long payloadsSinceDecommit)
    {
        int loadPercent = _gcStrategy.DecommitMemoryLoadPercent;
        if (loadPercent <= 0) return default;

        GcMemoryLoad load = _runtime.GetMemoryLoad();
        int unconditional = _gcStrategy.CollectionsPerUnconditionalDecommit;
        DecommitDecision decision =
            load.TotalMemoryBytes <= 0 ? DecommitDecision.LoadUnknown
            // The derived total can be a byte short of the runtime's; one part in 10^4 covers that and keeps "at or above".
            : load.MemoryLoadBytes * 100 + load.TotalMemoryBytes / 10_000 >= load.TotalMemoryBytes * loadPercent ? DecommitDecision.Pressure
            : unconditional >= 0 && payloadsSinceDecommit >= unconditional ? DecommitDecision.SafetyNet
            : DecommitDecision.Skip;

        DecommitCheck check = new(decision, load, loadPercent, unconditional, payloadsSinceDecommit);
        if (decision == DecommitDecision.Skip) LogDecommitDecision(check);
        return check;
    }

    /// <summary>Logs a decision when it differs from the last one logged, rather than at every payload: a skip repeats after each one until it ends.</summary>
    private void LogDecommitDecision(in DecommitCheck check)
    {
        if (check.Decision == DecommitDecision.None || check.Decision == _lastLoggedDecommitDecision) return;
        _lastLoggedDecommitDecision = check.Decision;
        if (_logger.IsInfo) _logger.Info(DescribeDecommitDecision(check.Decision, check.Load, check.LoadPercent, check.Unconditional, check.Payloads));
    }

    private static string DescribeDecommitDecision(DecommitDecision decision, GcMemoryLoad load, int loadPercent, int unconditional, long payloads)
    {
        string memory = load.TotalMemoryBytes > 0
            ? $"memory load {load.MemoryLoadBytes * 100.0 / load.TotalMemoryBytes:F0}% of {load.TotalMemoryBytes / (1024.0 * 1024 * 1024):F1} GiB"
            : "memory load unknown";
        return decision switch
        {
            DecommitDecision.Pressure => $"GC decommit: {memory} is at or above {loadPercent}%, releasing memory after {payloads} payloads.",
            DecommitDecision.SafetyNet => $"GC decommit: {memory} is below {loadPercent}%, releasing memory anyway after {payloads} payloads (unconditional after {unconditional}).",
            DecommitDecision.LoadUnknown => $"GC decommit: {memory}, releasing memory after {payloads} payloads.",
            _ => unconditional >= 0
                ? $"GC decommit deferred: {memory} is below {loadPercent}%; releasing memory once it is reached or after {unconditional} payloads."
                : $"GC decommit deferred: {memory} is below {loadPercent}%; releasing memory once it is reached.",
        };
    }

    private enum CollectionClaim { Granted, Cancelled, Busy, Throttled }

    /// <summary>Claims a collection after all cancellable waits; never holds the gate during runtime collection.</summary>
    /// <remarks>
    /// Busy while a region is active or a payload is still in processing: a payload that starts while another is in
    /// processing may find the <see cref="GCScheduler"/> already paused and so not hold it, and without a region of its
    /// own (<see cref="NoGcRegionMode.Never"/>, or a third overlapping payload) nothing else keeps the collection out of it.
    /// </remarks>
    /// <param name="keepCancellable">Leaves the pending collection cancellable, for a re-commit or decommit still to come or running.</param>
    /// <param name="exemptFromSpacing">Skips the minimum spacing, for a decommit that follows its own ordinary collection.</param>
    private CollectionClaim ClaimCollection(CancellationTokenSource pendingGcCts, bool throttle, bool keepCancellable, bool exemptFromSpacing)
    {
        lock (_lock)
        {
            if (pendingGcCts.IsCancellationRequested) return CollectionClaim.Cancelled;
            if (_runtime.IsActive || _payloadsInProcessing > 0) return CollectionClaim.Busy;
            if (throttle)
            {
                long timeStamp = NowMs();
                long minMsBetweenCollections = EffectiveMinMsBetweenCollections();
                Metrics.GcKeeperMinMsBetweenCollections = minMsBetweenCollections;
                if (!exemptFromSpacing && _lastGcTimeMs is long lastGcTimeMs && timeStamp - lastGcTimeMs <= minMsBetweenCollections)
                {
                    return CollectionClaim.Throttled;
                }

                _lastGcTimeMs = timeStamp;
            }

            if (!keepCancellable && ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
            return CollectionClaim.Granted;
        }
    }

    private static void CountPostBlockClaim(CollectionClaim claim)
    {
        switch (claim)
        {
            case CollectionClaim.Cancelled: Interlocked.Increment(ref Metrics.GcKeeperPostBlockCancelled); break;
            case CollectionClaim.Busy: Interlocked.Increment(ref Metrics.GcKeeperPostBlockSkipped); break;
            case CollectionClaim.Throttled: Interlocked.Increment(ref Metrics.GcKeeperPostBlockThrottled); break;
        }
    }

    private void ScheduleGC()
    {
        if (_gcScheduleTask.IsCompleted)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_gcScheduleTask.IsCompleted)
                {
                    _gcScheduleTask = ScheduleGCInternal(throttle: true);
                }
            }
        }
    }

    internal async Task ScheduleGCInternal(bool throttle)
    {
        (GcLevel generation, GcCompaction compacting) = _gcStrategy.GetForcedGCParams();
        if (generation > GcLevel.NoGC)
        {
            CancellationTokenSource pendingGcCts;
            int postBlockDelayMs;
            lock (_lock)
            {
                if (_disposed) return;
                _pendingGcCts = pendingGcCts = new();
                postBlockDelayMs = EffectivePostBlockDelayMs();
                Metrics.GcKeeperPostBlockDelayMs = postBlockDelayMs;
            }

            try
            {
                // Leave time for the Engine API response before attempting ordinary collection.
                if (postBlockDelayMs <= 0)
                {
                    // Always async
                    await Task.Yield();
                }
                else
                {
                    // A completed delay must not run collection under ScheduleGC's lock.
                    if (!await _delay(postBlockDelayMs, pendingGcCts.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding))
                    {
                        Interlocked.Increment(ref Metrics.GcKeeperPostBlockCancelled);
                        return;
                    }
                }

                if (pendingGcCts.IsCancellationRequested)
                {
                    Interlocked.Increment(ref Metrics.GcKeeperPostBlockCancelled);
                    return;
                }
                if (_runtime.IsActive)
                {
                    Interlocked.Increment(ref Metrics.GcKeeperPostBlockSkipped);
                }
                else
                {
                    long payloadsSinceDecommit = Interlocked.Read(ref _payloadsSinceDecommit);
                    int collectionsPerDecommit = _gcStrategy.CollectionsPerDecommit;

                    DecommitCheck decommitCheck = default;
                    bool decommit = false;
                    if (collectionsPerDecommit >= 0 && payloadsSinceDecommit >= collectionsPerDecommit)
                    {
                        decommitCheck = CheckDecommit(payloadsSinceDecommit);
                        decommit = decommitCheck.Decision != DecommitDecision.Skip;
                    }

                    // A due decommit waits for a longer idle gap, which payloads arriving faster than that never leave: the
                    // ordinary collection runs first, so the wait cannot cost the block its own. Without a wait left, the
                    // decommit's full collection covers it.
                    int remainingIdleMs = decommit ? DecommitIdleDelayMs - Math.Max(0, postBlockDelayMs) : 0;
                    if (!decommit || remainingIdleMs > 0)
                    {
                        // A throttled collection does not hold up the decommit, whose idle wait is longer than the spacing.
                        // Cancellable while payloads enter a region, so the next payload still calls off the re-commit.
                        CollectionClaim claim = ClaimCollection(pendingGcCts, throttle, keepCancellable: decommit || _entersRegion, exemptFromSpacing: false);
                        CountPostBlockClaim(claim);
                        if (claim is CollectionClaim.Cancelled or CollectionClaim.Busy) return;
                        if (claim == CollectionClaim.Granted)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {generation}, compacting {compacting}");
                            // Declined by the scheduler: another collection is running, or forced ones are excluded.
                            if (_runtime.Collect(generation, GCCollectionMode.Forced, compacting))
                            {
                                Interlocked.Increment(ref Metrics.GcKeeperPostBlockCollections);
                                RecommitAfterCollection(pendingGcCts);
                            }
                            else
                            {
                                Interlocked.Increment(ref Metrics.GcKeeperPostBlockSkipped);
                            }
                        }
                    }

                    if (!decommit) return;

                    // Keep the debt pending through a longer idle gap; new payloads cancel this wait and the decommit stays due.
                    if (remainingIdleMs > 0 && !await _delay(remainingIdleMs, pendingGcCts.Token))
                    {
                        Interlocked.Increment(ref Metrics.GcKeeperDecommitIdleWaitsCancelled);
                        return;
                    }

                    // Not spaced from the ordinary collection that ran just before it; a decommit stays cancellable through
                    // its collection, so the next payload still calls off the re-commit.
                    CollectionClaim decommitClaim = ClaimCollection(pendingGcCts, throttle, keepCancellable: true, exemptFromSpacing: true);
                    if (decommitClaim != CollectionClaim.Granted)
                    {
                        // Without its own wait the decommit stands in for the ordinary collection, and is counted as one.
                        if (remainingIdleMs > 0) Interlocked.Increment(ref Metrics.GcKeeperDecommitIdleWaitsCancelled);
                        else CountPostBlockClaim(decommitClaim);
                        return;
                    }

                    // Also decommit memory back to O/S
                    if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {GcLevel.Gen2}, compacting {GcCompaction.Full}, decommitting");
                    if (_runtime.Collect(GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full))
                    {
                        Interlocked.Increment(ref Metrics.GcKeeperDecommits);
                        Interlocked.Add(ref _payloadsSinceDecommit, -payloadsSinceDecommit);
                        RecommitAfterCollection(pendingGcCts);
                        // Only now: a payload can cancel the idle wait above, and the retry may run under another decision.
                        LogDecommitDecision(decommitCheck);
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
