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
    // Idle mainnet nodes allocate about 1.5 MB/s between blocks; under eth_call load, more than 100 MB/s, a throwaway
    // entry after every collection had to collect itself (51 ms on average at 8 concurrent calls).
    private static readonly long QuietBytesPerSecond = 8.MB;
    // A throwaway entry right after a collection on a quiet node took 0.25 ms on a mainnet node and up to 2.8 ms in a
    // synthetic 16-heap probe; one that has to collect first is caught by the collection check, not by this bound.
    private static readonly TimeSpan SlowRearm = TimeSpan.FromMilliseconds(10);
    private const int RearmBackoffPayloads = 25;
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
    private readonly TimeProvider _time;
    // Where the last payload's processing window ended: allocated bytes and timestamp; null before any has ended.
    private (long Allocated, long Timestamp)? _lastPayloadEnd;
    private int _rearmPausePayloadsLeft;
    private ArmedBudget? _armed;
    // Held across a payload's region entry, and across a throwaway region's entry and end, so the two never overlap.
    private readonly Lock _runtimeLock = new();

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
        : this(gcStrategy, logManager, GcRegionRuntime.Instance) { }

    internal GCKeeper(IGCStrategy gcStrategy, ILogManager logManager, IGcRegionRuntime runtime,
        Action<IThreadPoolWorkItem>? queue = null, Func<int, CancellationToken, Task<bool>>? delay = null,
        TimeProvider? time = null)
    {
        _gcStrategy = gcStrategy;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _logger = logManager.GetClassLogger<GCKeeper>();
        _runtime = runtime;
        _delay = delay ?? TaskExtensions.DelaySafe;
        _time = time ?? TimeProvider.System;
        // One outstanding entry bounds pool usage without a dedicated thread for each keeper.
        _queue = queue ?? (static item => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false));
    }

    /// <summary>The default of <see cref="IGCStrategy.NoGCRegionGuardBytes"/>.</summary>
    /// <remarks>
    /// A re-arm leaves the heaps about 538 MB of gen0 and 67 MB of LOH budget, so with at most this much allocated since,
    /// a block that skips its entry still has at least about 500 MB of gen0 and 35 MB of LOH budget, even if all of it
    /// was large objects.
    /// </remarks>
    internal static readonly long DefaultGuardSlack = 32.MB;

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
        if (!eligible) return StartPayloadRegion(eligible, NoGcRegionMode.Never, armed: false, out _);

        // Sampled before the entry is queued, so a collection the entry has to wait for counts as one in processing.
        ProcessingSample start = SampleProcessing();
        NoGcRegionMode mode = _gcStrategy.NoGCRegionMode;
        bool guard = mode == NoGcRegionMode.Guard;
        // Read before the keeper's lock is taken: the GC index allocates.
        bool armed = guard && IsBudgetArmed(start);
        IDisposable lease = StartPayloadRegion(eligible, mode, armed, out bool skipped);
        return new ProcessingWindow(this, lease, start, guard, skippedByGuard: skipped && guard);
    }

    /// <param name="mode">Whether the payload enters a region of its own when no other payload holds one.</param>
    /// <param name="armed">Whether the guard found the region's budget armed (see <see cref="IsBudgetArmed"/>).</param>
    /// <param name="skipped">Whether the payload runs without any region because <paramref name="mode"/> kept it out.</param>
    private IDisposable StartPayloadRegion(bool eligible, NoGcRegionMode mode, bool armed, out bool skipped)
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
            if (_rearmPausePayloadsLeft > 0) _rearmPausePayloadsLeft--;
            if (_region is not null)
            {
                // A payload that starts while the previous one is still inside its region shares that region rather
                // than running unprotected: it then ends when the last payload leaves, not when the first returns.
                // The newcomer keeps a lease of its own so its scheduler pause and its collection stay its own.
                if (_region.TryAddLease()) return new SharedRegionLease(_region, region);
                if (_logger.IsDebug) _logger.Debug("No-GC region entry skipped: previous entry or region is still active.");
                return region;
            }
            // Decided only by a payload that would start a region of its own: one joining a region needs no budget.
            bool enter = mode switch
            {
                NoGcRegionMode.Never => false,
                NoGcRegionMode.Guard => !armed,
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
    /// Whether the region's budget was re-armed after the last runtime collection and no more than the slack
    /// (<see cref="IGCStrategy.NoGCRegionGuardBytes"/>) was allocated since, so the block can run without an entry of its own.
    /// </summary>
    /// <param name="now">Where the payload's window starts.</param>
    /// <remarks>
    /// Entering a region sets gen0's and LOH's budget to the region's per-heap allowance, and ending it does not restore
    /// them until a collection recomputes them (dotnet/runtime v10.0.0, gc.cpp: set_soh_allocations_for_no_gc,
    /// set_loh_allocations_for_no_gc, restore_data_for_no_gc). Only a throwaway entry arms the next payload; a
    /// payload's own entry is followed by its post-block collection, which disarms it.
    /// </remarks>
    private bool IsBudgetArmed(in ProcessingSample now)
    {
        ArmedBudget? armed = Volatile.Read(ref _armed);
        if (armed is null) return false;
        long slack = _gcStrategy.NoGCRegionGuardBytes > 0 ? _gcStrategy.NoGCRegionGuardBytes : DefaultGuardSlack;
        return _runtime.LastGcIndex == armed.GcIndex
            && !RuntimeCollected(armed.Sample, now)
            && now.Allocated - armed.Sample.Allocated <= slack;
    }

    /// <summary>Where a throwaway entry left the region's budget: right after it, and the index of the last collection then.</summary>
    private sealed record ArmedBudget(ProcessingSample Sample, long GcIndex);

    // Counted here rather than when queued: the runtime can still decline, and the payload can end before the entry runs.
    private static void OnRegionEntered() => Interlocked.Increment(ref Metrics.NoGcRegionEntries);

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

    /// <summary>Whether the runtime collected between two samples, other than by the keeper's own region entries.</summary>
    /// <remarks>
    /// An own entry that moved the collection count between the two samples' reads of it started before the end
    /// sample read the started count, and finished after the start sample read the done count, so at most
    /// end.OwnEntriesStarted - start.OwnEntriesDone of the collections counted are own entries. This never reports a
    /// false collection; an own entry overlapping an edge of the window can hide a runtime collection in it (rare).
    /// </remarks>
    private static bool RuntimeCollected(in ProcessingSample start, in ProcessingSample end) =>
        end.Collections - start.Collections > end.OwnEntriesStarted - start.OwnEntriesDone;

    /// <summary>
    /// Records where the payload's window ended for the guard's quiet check, and counts the payload if the runtime
    /// collected between its start and the end of its lease, other than by entering a region.
    /// </summary>
    private void EndProcessingWindow(in ProcessingSample start, bool guard, bool skippedByGuard)
    {
        ProcessingSample end = SampleProcessing();
        if (guard)
        {
            long timestamp = _time.GetTimestamp();
            lock (_lock) _lastPayloadEnd = (end.Allocated, timestamp);
        }
        if (!RuntimeCollected(start, end)) return;
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
                // Waits for a re-commit or a re-arm to end its throwaway region (see Recommit).
                lock (keeper._runtimeLock)
                {
                    started = keeper.TryStartRuntimeRegion();
                    if (started) OnRegionEntered();
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
    /// Enters and at once ends a no-GC region right after a post-block collection: after a decommit, so the next
    /// payload's entry finds its budget committed rather than committing it inside its own suspension, and on a quiet
    /// node with <see cref="NoGcRegionMode.Guard"/> (a re-arm), so the next payload can skip its entry altogether.
    /// </summary>
    /// <param name="rearm">Whether this is a re-arm after an ordinary collection rather than the re-commit after a decommit.</param>
    /// <remarks>
    /// <para>The aggressive collection decommits every free region, so the entry after it commits the whole budget
    /// while every thread is suspended, on the payload's path. Ending a region leaves the regions it linked committed
    /// until the next collection (dotnet/runtime v10.0.0, gc.cpp: extend_soh_for_no_gc commits without touching the
    /// pages, and decommit only takes regions a collection released), so the next entry reuses them and RSS does not
    /// grow until they are allocated in. The decommit still returns everything else.</para>
    /// <para>Either entry leaves the heaps the region's budget until the next collection (see <see cref="IsBudgetArmed"/>).
    /// Right after a collection on a quiet node a re-arm is short; under load the entry has to collect itself, so a
    /// re-arm that takes longer than <see cref="SlowRearm"/>, or during which the runtime collected,
    /// pauses re-arms for the next <see cref="RearmBackoffPayloads"/> payloads.</para>
    /// <para>Skipped once the next payload has cancelled the pending collection, while a region is pending or active,
    /// when the strategy disallows regions or its mode is <see cref="NoGcRegionMode.Never"/>, and on shutdown. A
    /// payload admitted meanwhile queues its entry, which waits on <see cref="_runtimeLock"/> until the throwaway
    /// region has ended.</para>
    /// </remarks>
    private void Recommit(CancellationTokenSource pendingGcCts, bool rearm)
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

        bool slow = false;
        bool collected = false;
        TimeSpan took = TimeSpan.Zero;
        try
        {
            ProcessingSample before = SampleProcessing();
            long gcIndexBefore = _runtime.LastGcIndex;
            long startedAt = _time.GetTimestamp();
            if (TryStartRuntimeRegion())
            {
                // Not a payload's entry, so NoGcRegionEntries leaves it out; it moves GC.CollectionCount as any entry
                // does, so TryStartRuntimeRegion counts it as an own entry for payloads in processing.
                if (rearm) Interlocked.Increment(ref Metrics.NoGcRegionRearms);
                else Interlocked.Increment(ref Metrics.NoGcRegionRecommits);
                if (_runtime.IsActive) _runtime.End();
                took = _time.GetElapsedTime(startedAt);
                ProcessingSample after = SampleProcessing();
                long gcIndex = _runtime.LastGcIndex;
                // An entry that has to make room collects first, which moves the index as any collection does.
                collected = gcIndex != gcIndexBefore || RuntimeCollected(before, after);
                slow = took > SlowRearm;
                Volatile.Write(ref _armed, new ArmedBudget(after, gcIndex));
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

        if (!rearm || !(slow || collected)) return;
        lock (_lock) _rearmPausePayloadsLeft = RearmBackoffPayloads;
        Interlocked.Increment(ref Metrics.NoGcRegionRearmBackoffs);
        if (_logger.IsInfo) _logger.Info($"No-GC region re-arm took {took.TotalMilliseconds:F2} ms, runtime collected during it: {collected}; re-arms paused for the next {RearmBackoffPayloads} payloads.");
    }

    /// <summary>Whether the node allocated at most <see cref="QuietBytesPerSecond"/> since the last payload's window ended.</summary>
    /// <param name="allocated">Allocated bytes now.</param>
    /// <param name="timestamp">The timestamp now.</param>
    /// <remarks>No payload ended yet reads as busy.</remarks>
    private bool IsQuietLocked(long allocated, long timestamp) =>
        _lastPayloadEnd is (long endAllocated, long endTimestamp)
        && allocated - endAllocated <= QuietBytesPerSecond * _time.GetElapsedTime(endTimestamp, timestamp).TotalSeconds;

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
                    bool guard = !decommit && _gcStrategy.NoGCRegionMode == NoGcRegionMode.Guard;
                    long allocated = _runtime.AllocatedBytes;
                    long timestamp = _time.GetTimestamp();
                    bool quiet = false;
                    bool rearm = false;
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

                        if (guard)
                        {
                            quiet = IsQuietLocked(allocated, timestamp);
                            rearm = quiet && _rearmPausePayloadsLeft == 0;
                        }

                        // A decommit or a re-arm stays cancellable through its collection: the next payload still calls
                        // off the throwaway entry after it.
                        if (!decommit && !rearm && ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
                    }

                    if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {generation}, compacting {compacting}");
                    bool collected = _runtime.Collect(generation, mode, compacting);
                    if (collected && decommit)
                    {
                        Interlocked.Add(ref _payloadsSinceDecommit, -payloadsSinceDecommit);
                        Recommit(pendingGcCts, rearm: false);
                    }
                    else if (collected && rearm)
                    {
                        Recommit(pendingGcCts, rearm: true);
                    }
                    else if (collected && guard && !quiet)
                    {
                        Interlocked.Increment(ref Metrics.NoGcRegionRearmsSkippedBusy);
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
