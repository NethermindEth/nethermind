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
    private const int MinMsBetweenCollections = 3_000;
    private long _payloadsSinceDecommit;
    private readonly Lock _lock = new();
    private readonly IGCStrategy _gcStrategy;
    private readonly IGcRegionRuntime _runtime;
    private readonly Action<IThreadPoolWorkItem> _queue;
    private readonly Func<int, CancellationToken, Task<bool>> _delay;
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
    private int _pendingEntries;
    private TaskCompletionSource? _entriesDrained;
    // Region entries this keeper started and finished, whatever their result; each entry moves GC.CollectionCount
    // without collecting anything (see EndProcessingWindow).
    private long _ownEntriesStarted;
    private long _ownEntriesDone;
    private readonly Gen0BudgetTracker _budget = new();
    private readonly BlockAllocationTracker _blockAllocation = new();
    // Held across a payload's region entry, and across the re-commit's entry and end, so the two never overlap.
    private readonly Lock _runtimeLock = new();

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
        : this(gcStrategy, logManager, GcRegionRuntime.Instance) { }

    internal GCKeeper(IGCStrategy gcStrategy, ILogManager logManager, IGcRegionRuntime runtime,
        Action<IThreadPoolWorkItem>? queue = null, Func<int, CancellationToken, Task<bool>>? delay = null)
    {
        _gcStrategy = gcStrategy;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _logger = logManager.GetClassLogger<GCKeeper>();
        _runtime = runtime;
        _delay = delay ?? TaskExtensions.DelaySafe;
        // One outstanding entry bounds pool usage without a dedicated thread for each keeper.
        _queue = queue ?? (static item => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false));
        if (gcStrategy.NoGCRegionMode == NoGcRegionMode.Guard && !runtime.CanReadGen0Budget && _logger.IsWarn)
        {
            _logger.Warn("No-GC region guard unavailable: the runtime does not expose gen0's allocation budget, so engine_newPayload enters the no-GC region every time, as with Merge.NoGcRegionOnNewPayload=Always.");
        }
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

    /// <summary>Cancels the delayed collection if it has not yet been claimed for execution.</summary>
    public void CancelPendingGC()
    {
        lock (_lock)
        {
            _pendingGcCts?.Cancel();
        }
    }

    /// <summary>Queues no-GC-region entry without waiting for the runtime; disposing the lease ends its protection.</summary>
    /// <remarks>
    /// Whether the payload enters a region of its own follows <see cref="IGCStrategy.NoGCRegionMode"/>. A payload that
    /// does not still shares a region another payload holds, and disposing its lease still schedules the post-block
    /// collection, which is what leaves gen0's budget fresh for the next payload.
    /// </remarks>
    public IDisposable TryStartNoGCRegion()
    {
        bool eligible = _gcStrategy.CanStartNoGCRegion();
        if (!eligible) return StartPayloadRegion(eligible, NoGcRegionMode.Never, allocated: 0, out _);

        // Sampled before the entry is queued, so a collection the entry has to wait for counts as one in processing.
        ProcessingSample start = SampleProcessing();
        NoGcRegionMode mode = _gcStrategy.NoGCRegionMode;
        bool guard = mode == NoGcRegionMode.Guard;
        IDisposable lease = StartPayloadRegion(eligible, mode, start.Allocated, out bool skipped);
        return new ProcessingWindow(this, lease, start, guard, skippedByGuard: skipped && guard);
    }

    /// <param name="mode">Whether the payload enters a region of its own when no other payload holds one.</param>
    /// <param name="allocated">Allocated bytes where the payload's window starts, for the guard.</param>
    /// <param name="skipped">Whether the payload runs without any region because <paramref name="mode"/> kept it out.</param>
    private IDisposable StartPayloadRegion(bool eligible, NoGcRegionMode mode, long allocated, out bool skipped)
    {
        skipped = false;
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
                // A payload that starts while the previous one is still inside its region shares that region rather
                // than running unprotected: it then ends when the last payload leaves, not when the first returns.
                // The newcomer keeps a lease of its own so its scheduler pause and its collection stay its own.
                if (_region.TryAddLease()) return new SharedRegionLease(_region, region);
                if (_logger.IsDebug) _logger.Debug("No-GC region entry skipped: previous entry or region is still active.");
                return region;
            }
            // Decided only by a payload that would start a region of its own, so the guard's gauges show decisions
            // that took effect. The trackers' locks are taken under _lock here and never the other way round.
            bool enter = mode switch
            {
                NoGcRegionMode.Never => false,
                NoGcRegionMode.Guard => !BudgetCoversBlock(allocated),
                _ => true,
            };
            if (!enter)
            {
                skipped = true;
                Interlocked.Increment(ref Metrics.NoGcRegionSkips);
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

    /// <summary>
    /// Whether the gen0 allocation budget left, as estimated by <see cref="Gen0BudgetTracker"/>, is at least the
    /// guard's threshold (<see cref="GuardThreshold"/>), so the block is expected to run without a gen0 collection.
    /// </summary>
    /// <param name="allocated">Allocated bytes where the payload's window starts.</param>
    /// <remarks>An unknown budget never covers the block, so the region is entered as it always was.</remarks>
    private bool BudgetCoversBlock(long allocated)
    {
        long left = _budget.EstimateLeft(allocated, _runtime.LastGcIndex, _runtime.Gen0Budget, out long gen0Budget);
        long blockAllocation = _blockAllocation.Maximum;
        long threshold = GuardThreshold(_gcStrategy.NoGCRegionGuardBytes, gen0Budget, blockAllocation);
        bool known = left != Gen0BudgetTracker.Unknown;
        Metrics.NoGcRegionGuardThresholdBytes = threshold;
        Metrics.NoGcRegionGuardBudgetLeftBytes = known ? left : 0;
        Metrics.NoGcRegionGuardGen0BudgetBytes = gen0Budget;
        Metrics.NoGcRegionGuardBlockAllocationBytes = blockAllocation;
        return known && left >= threshold;
    }

    /// <summary>The gen0 budget that has to be left for the guard to skip the region.</summary>
    /// <param name="fixedBytes"><see cref="IGCStrategy.NoGCRegionGuardBytes"/>: a positive value replaces the rule.</param>
    /// <param name="gen0Budget">The budget the estimate starts from (B0), 0 when unknown.</param>
    /// <param name="blockAllocation">The most a payload's window allocated lately (A).</param>
    /// <remarks>
    /// T = max(3/4 x B0, 2 x A, <see cref="GuardFloor"/>). B0 is the budget the runtime derives from the L3 cache and
    /// the core count (5/8 of L3 per Server GC heap), or the region's own after the keeper's entry, and a quarter of
    /// it is the margin kept on it; 2 x A leaves room for twice the largest payload seen lately, whatever the budget.
    /// </remarks>
    internal static long GuardThreshold(long fixedBytes, long gen0Budget, long blockAllocation) =>
        fixedBytes > 0 ? fixedBytes : Math.Max(GuardFloor, Math.Max(gen0Budget * 3 / 4, 2 * blockAllocation));

    // Half of the small-object budget a region guarantees (Gen0BudgetTracker.RegionSohBudget, computed here rather than
    // read from the nested type so that the two type initializers cannot wait on each other): skip only when at least
    // half of what the region would guarantee is left.
    internal static readonly long GuardFloor = (_defaultSize - _lohSize) / 2;

    private void OnRegionEntered()
    {
        // Counted here rather than when queued: the runtime can still decline, and the payload can end before the entry runs.
        Interlocked.Increment(ref Metrics.NoGcRegionEntries);
        if (_gcStrategy.NoGCRegionMode == NoGcRegionMode.Guard) _budget.OnRegionEntered(_runtime.AllocatedBytes, _runtime.LastGcIndex);
    }

    /// <summary>The throwaway region of <see cref="RecommitAfterDecommit"/> was entered.</summary>
    /// <remarks>
    /// Not a payload's entry, so <see cref="Metrics.NoGcRegionEntries"/> leaves it out. It moves GC.CollectionCount as
    /// any entry does, so <see cref="TryStartRuntimeRegion"/> counts it as an own entry, which keeps a payload in
    /// processing from taking it for a runtime collection. It hands the heaps the region's budget as any entry does,
    /// and its end does not restore gen0's, so the guard's estimate follows it.
    /// </remarks>
    private void OnRecommitEntered()
    {
        Interlocked.Increment(ref Metrics.NoGcRegionRecommits);
        if (_gcStrategy.NoGCRegionMode == NoGcRegionMode.Guard) _budget.OnRegionEntered(_runtime.AllocatedBytes, _runtime.LastGcIndex);
    }

    private bool TryStartRuntimeRegion()
    {
        Interlocked.Increment(ref _ownEntriesStarted);
        try
        {
            return _runtime.TryStart(_defaultSize, _lohSize);
        }
        finally
        {
            Interlocked.Increment(ref _ownEntriesDone);
        }
    }

    /// <summary>Where a payload starts or ends: the keeper's entries finished, the collections the runtime has counted (those entries among them), the keeper's entries started, and the bytes allocated.</summary>
    private readonly record struct ProcessingSample(long OwnEntriesDone, int Collections, long OwnEntriesStarted, long Allocated);

    // Read in this order, see EndProcessingWindow.
    private ProcessingSample SampleProcessing()
    {
        long done = Interlocked.Read(ref _ownEntriesDone);
        int collections = _runtime.CollectionCount;
        long started = Interlocked.Read(ref _ownEntriesStarted);
        return new(done, collections, started, _runtime.AllocatedBytes);
    }

    /// <summary>
    /// Records what was allocated between the payload's start and the end of its lease for the guard, and counts the
    /// payload if the runtime collected in that time, other than by entering a region.
    /// </summary>
    /// <remarks>
    /// An own entry that moved the collection count between the two samples' reads of it started before the end
    /// sample read the started count, and finished after the start sample read the done count, so at most
    /// end.OwnEntriesStarted - start.OwnEntriesDone of the collections counted are own entries. This never reports a
    /// false collection; an own entry overlapping an edge of the window can hide a runtime collection in it (rare).
    /// </remarks>
    private void EndProcessingWindow(in ProcessingSample start, bool guard, bool skippedByGuard)
    {
        ProcessingSample end = SampleProcessing();
        if (guard) _blockAllocation.Record(end.Allocated - start.Allocated);
        if (end.Collections - start.Collections <= end.OwnEntriesStarted - start.OwnEntriesDone) return;
        Interlocked.Increment(ref Metrics.NewPayloadsWithCollection);
        if (skippedByGuard) Interlocked.Increment(ref Metrics.NoGcRegionGuardMisses);
    }

    /// <summary>A payload's lease that closes its processing window before ending the region or lease it wraps.</summary>
    private sealed class ProcessingWindow(GCKeeper keeper, IDisposable lease, ProcessingSample start, bool guard, bool skippedByGuard) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                keeper.EndProcessingWindow(start, guard, skippedByGuard);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    /// <summary>Estimates the gen0 allocation budget the runtime has left before its next gen0 collection.</summary>
    /// <remarks>
    /// <para>left = budget - (allocated now - allocated at the last collection), where the budget is gen0's budget
    /// summed over the heaps (<see cref="IGcRegionRuntime.Gen0Budget"/>) and a collection is seen as a change of
    /// <see cref="IGcRegionRuntime.LastGcIndex"/>, which region entries do not move. Collections are only seen when
    /// sampled, so one seen first is assumed to have run right after the previous sample (the most allocation it can
    /// have missed), except for the keeper's own collection, whose start is known.</para>
    /// <para>A region replaces the budget left with its own per-heap budget and its end does not restore it
    /// (dotnet/runtime v10.0.0 gc.cpp: set_soh_allocations_for_no_gc, restore_data_for_no_gc), so from a region's entry
    /// to the next collection what is left is the region's small-object budget less what was allocated since.</para>
    /// <para>Limits: the runtime collects when one heap's budget runs out, so the sum is optimistic when allocation is
    /// skewed across heaps; allocated bytes include large objects, which draw on their own budget (pessimistic); a
    /// background collection in progress and gen2 or LOH triggers are not modelled. Until the first collection is
    /// seen, everything allocated since start counts.</para>
    /// </remarks>
    internal sealed class Gen0BudgetTracker
    {
        public const long Unknown = long.MinValue;
        // The small-object budget a region of the keeper's size hands the heaps, summed (the runtime's 5% left out).
        internal static readonly long RegionSohBudget = _defaultSize - _lohSize;
        private readonly Lock _lock = new();
        private long _seenGcIndex = -1;
        private long _lastSampleAllocated;
        private long _allocatedAtGc;
        private bool _regionSinceGc;
        private long _allocatedAtRegionEntry;

        /// <param name="startBudget">The budget the estimate starts from: the runtime's, or the region's since its entry; 0 when unknown.</param>
        /// <returns>The estimated bytes left (may be negative), or <see cref="Unknown"/> when the budget is unknown.</returns>
        public long EstimateLeft(long allocated, long gcIndex, long budget, out long startBudget)
        {
            lock (_lock)
            {
                ObserveLocked(allocated, gcIndex);
                if (budget <= 0)
                {
                    startBudget = 0;
                    return Unknown;
                }
                startBudget = _regionSinceGc ? RegionSohBudget : budget;
                return startBudget - (allocated - (_regionSinceGc ? _allocatedAtRegionEntry : _allocatedAtGc));
            }
        }

        /// <param name="allocatedBefore">Allocated bytes read right before the collection started.</param>
        public void OnCollected(long allocatedBefore, long allocated, long gcIndex)
        {
            lock (_lock)
            {
                if (gcIndex != _seenGcIndex)
                {
                    _seenGcIndex = gcIndex;
                    _allocatedAtGc = allocatedBefore;
                    _regionSinceGc = false;
                }
                _lastSampleAllocated = allocated;
            }
        }

        public void OnRegionEntered(long allocated, long gcIndex)
        {
            lock (_lock)
            {
                // A collection seen now ran before the entry: one inside a region ends it, and is seen after this.
                ObserveLocked(allocated, gcIndex);
                _regionSinceGc = true;
                _allocatedAtRegionEntry = allocated;
            }
        }

        private void ObserveLocked(long allocated, long gcIndex)
        {
            if (gcIndex != _seenGcIndex)
            {
                _seenGcIndex = gcIndex;
                _allocatedAtGc = _lastSampleAllocated;
                _regionSinceGc = false;
            }
            _lastSampleAllocated = allocated;
        }
    }

    /// <summary>Keeps the rolling maximum of the process-wide bytes allocated during a payload's window.</summary>
    /// <remarks>
    /// <para>A window runs from the guard's decision to the end of the payload's lease, so it covers the block and
    /// everything the lease is held for. Overlapping windows count the same allocation twice, which only raises the
    /// maximum.</para>
    /// <para>The maximum is kept in two buckets of <see cref="BucketPayloads"/> payloads, so it covers the last 300
    /// to 600 of them, one to two hours at 12 s slots: a heavy block raises the threshold for that long, then drops
    /// out. The first <see cref="WarmUpPayloads"/> after start are left out: they fill caches and compile code that
    /// later payloads find ready.</para>
    /// </remarks>
    internal sealed class BlockAllocationTracker
    {
        internal const int BucketPayloads = 300;
        internal const int WarmUpPayloads = 20;
        private readonly Lock _lock = new();
        private int _warmUpLeft = WarmUpPayloads;
        private int _inBucket;
        private long _current;
        private long _previous;

        /// <summary>The largest allocation of a window in the current and the previous bucket, 0 before any is recorded.</summary>
        public long Maximum
        {
            get
            {
                lock (_lock) return Math.Max(_previous, _current);
            }
        }

        public void Record(long allocated)
        {
            lock (_lock)
            {
                if (_warmUpLeft > 0)
                {
                    _warmUpLeft--;
                    return;
                }
                _current = Math.Max(_current, allocated);
                if (++_inBucket < BucketPayloads) return;
                _previous = _current;
                _current = 0;
                _inBucket = 0;
            }
        }
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
                if (_released)
                {
                    Interlocked.Increment(ref Metrics.NoGcRegionSkips);
                    return;
                }
                _starting = true;
            }

            bool started = false;
            try
            {
                // Waits for a re-commit after decommit to end its region (see RecommitAfterDecommit).
                lock (keeper._runtimeLock)
                {
                    started = keeper.TryStartRuntimeRegion();
                    if (started) keeper.OnRegionEntered();
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

            if (started)
            {
                EndRegion();
            }
            else
            {
                Interlocked.Increment(ref Metrics.NoGcRegionSkips);
                keeper.ReleaseRegion(this);
            }
        }

        /// <summary>Released by the payload that admitted the region, after which it takes no new leases.</summary>
        public void Dispose() => Release(owner: true);

        /// <summary>Ends the region whatever is still leased, for the keeper's own shutdown.</summary>
        public void ForceRelease() => Release(force: true);

        public void Release(bool owner = false, bool force = false)
        {
            bool end;
            bool release;
            lock (_stateLock)
            {
                if (_released) return;
                if (owner) _ownerReleased = true;
                if (force) _leases = 0; else _leases--;
                // Still covering another payload: the region is not this lease's to end.
                if (_leases > 0) return;
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
    /// Enters and at once ends a no-GC region right after a decommit, so the next payload's entry finds its budget
    /// committed rather than committing it inside its own suspension.
    /// </summary>
    /// <remarks>
    /// <para>The aggressive collection decommits every free region, so the entry after it commits the whole budget
    /// while every thread is suspended, on the payload's path. Ending a region leaves the regions it linked committed
    /// until the next collection (dotnet/runtime v10.0.0, gc.cpp: extend_soh_for_no_gc commits without touching the
    /// pages, and decommit only takes regions a collection released), so the next entry reuses them and RSS does not
    /// grow until they are allocated in. The decommit still returns everything else.</para>
    /// <para>Skipped once the next payload has cancelled the pending collection, while a region is pending or active,
    /// when the strategy disallows regions or its mode is <see cref="NoGcRegionMode.Never"/>, and on shutdown. A
    /// payload admitted meanwhile queues its entry, which waits on <see cref="_runtimeLock"/> until the throwaway
    /// region has ended.</para>
    /// </remarks>
    private void RecommitAfterDecommit(CancellationTokenSource pendingGcCts)
    {
        // Never keeps the runtime out of regions altogether, a throwaway one included.
        bool allowed = _gcStrategy.NoGCRegionMode != NoGcRegionMode.Never && _gcStrategy.CanStartNoGCRegion();
        lock (_lock)
        {
            if (ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
            if (!allowed || pendingGcCts.IsCancellationRequested || _disposed || _region is not null || _runtime.IsActive) return;
            // Taken under the keeper's lock, so no entry can be between its admission and its runtime call here.
            _runtimeLock.Enter();
        }

        try
        {
            if (TryStartRuntimeRegion())
            {
                OnRecommitEntered();
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
            lock (_lock)
            {
                if (_disposed) return;
                _pendingGcCts = pendingGcCts = new();
            }

            try
            {
                // Leave time for the Engine API response before attempting ordinary collection.
                int postBlockDelayMs = _postBlockDelayMs;
                if (postBlockDelayMs <= 0)
                {
                    // Always async
                    await Task.Yield();
                }
                else
                {
                    // A completed delay must not run collection under ScheduleGC's lock.
                    if (!await _delay(postBlockDelayMs, pendingGcCts.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding)) return;
                }

                if (pendingGcCts.IsCancellationRequested) return;
                if (!_runtime.IsActive)
                {
                    long payloadsSinceDecommit = Interlocked.Read(ref _payloadsSinceDecommit);
                    int collectionsPerDecommit = _gcStrategy.CollectionsPerDecommit;

                    GCCollectionMode mode = GCCollectionMode.Forced;
                    bool decommit = collectionsPerDecommit >= 0 && payloadsSinceDecommit >= collectionsPerDecommit;
                    if (decommit)
                    {
                        // Keep the debt pending through a longer idle gap; new payloads cancel this wait.
                        int remainingIdleMs = DecommitIdleDelayMs - Math.Max(0, postBlockDelayMs);
                        if (remainingIdleMs > 0 && !await _delay(remainingIdleMs, pendingGcCts.Token)) return;

                        // Also decommit memory back to O/S
                        mode = GCCollectionMode.Aggressive;
                        generation = GcLevel.Gen2;
                        compacting = GcCompaction.Full;
                    }

                    // Claim only after all cancellable waits; never hold the gate during runtime collection.
                    lock (_lock)
                    {
                        if (pendingGcCts.IsCancellationRequested || _runtime.IsActive) return;
                        if (throttle)
                        {
                            long timeStamp = Environment.TickCount64;
                            if (_lastGcTimeMs is long lastGcTimeMs && timeStamp - lastGcTimeMs <= MinMsBetweenCollections)
                            {
                                return;
                            }

                            _lastGcTimeMs = timeStamp;
                        }

                        // A decommit stays cancellable through its collection: the next payload still calls off the re-commit.
                        if (!decommit && ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
                    }

                    if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {generation}, compacting {compacting}");
                    long allocatedBefore = _runtime.AllocatedBytes;
                    bool collected = _runtime.Collect(generation, mode, compacting);
                    if (collected && _gcStrategy.NoGCRegionMode == NoGcRegionMode.Guard)
                    {
                        _budget.OnCollected(allocatedBefore, _runtime.AllocatedBytes, _runtime.LastGcIndex);
                    }
                    if (collected && decommit)
                    {
                        Interlocked.Add(ref _payloadsSinceDecommit, -payloadsSinceDecommit);
                        RecommitAfterDecommit(pendingGcCts);
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
