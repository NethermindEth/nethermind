// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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

    // Entry ahead of the payload, see SchedulePrepareNoGCRegion.
    internal const int PreEntryDelayMs = 20;
    internal const int PreEntryTimeoutMs = 3_000;
    // A quarter of the SOH budget may go elsewhere before the payload takes the region over; the rest is the block's.
    internal static readonly long PreEntryMaxAllocatedBytes = (_defaultSize - _lohSize) / 4;
    private readonly Func<long> _timestamp;
    private readonly CancellationTokenSource _preEntryCts = new();
    private int _preEntryScheduled;
    // BENCH (bench/gc-region-pre-slot): entry ahead of the slot, see OnNewHead.
    private readonly PreEntrySettings _settings;
    private readonly Func<long> _unixTimeMs;
    private long _headTimestamp;
    private int _slotLoopRunning;
    // BENCH (bench/gc-region-guard): region entries this keeper made (payload, pre-entry, re-commit). Each one moves
    // GC.CollectionCount(0..2) by one without collecting anything, so they are taken out of the in-processing counts.
    private long _ownEntries;
    // Held across the runtime's region entry, and across the re-commit's entry and end, so the two never overlap.
    private readonly Lock _runtimeLock = new();
    private readonly Gen0BudgetTracker _budget = new();

    public GCKeeper(IGCStrategy gcStrategy, ILogManager logManager)
        : this(gcStrategy, logManager, GcRegionRuntime.Instance, settings: PreEntrySettings.FromEnvironment()) { }

    internal GCKeeper(IGCStrategy gcStrategy, ILogManager logManager, IGcRegionRuntime runtime,
        Action<IThreadPoolWorkItem>? queue = null, Func<int, CancellationToken, Task<bool>>? delay = null,
        Func<long>? timestamp = null, PreEntrySettings? settings = null, Func<long>? unixTimeMs = null)
    {
        _gcStrategy = gcStrategy;
        _postBlockDelayMs = gcStrategy.PostBlockDelayMs;
        _logger = logManager.GetClassLogger<GCKeeper>();
        _runtime = runtime;
        _delay = delay ?? TaskExtensions.DelaySafe;
        // One outstanding entry bounds pool usage without a dedicated thread for each keeper.
        _queue = queue ?? (static item => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _settings = settings ?? PreEntrySettings.Default;
        _unixTimeMs = unixTimeMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_logger.IsInfo) _logger.Info($"No-GC region pre-entry: {_settings}");
    }

    public void Dispose()
    {
        NoGCRegion? region;
        lock (_lock)
        {
            _disposed = true;
            _pendingGcCts?.Cancel();
            _preEntryCts.Cancel();
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
    /// A region entered ahead of the payload by <see cref="SchedulePrepareNoGCRegion"/> is taken over while it is still
    /// usable: the payload becomes its owner and the region ends with the payload's lease, as if the payload had
    /// entered it itself. One that is no longer usable is ended first and the payload enters a region of its own.
    /// </remarks>
    public IDisposable TryStartNoGCRegion()
    {
        bool eligible = _gcStrategy.CanStartNoGCRegion();
        if (!eligible) return StartPayloadRegion(eligible, enter: true);

        // BENCH: the processing window opens before the entry is queued, so a collection the entry waits for counts.
        ProcessingSample start = SampleProcessing();
        // BENCH: in guard and never modes, whether the gen0 budget left covers the block (never: what the guard would do).
        bool covered = _settings.Entry != RegionEntry.Always && BudgetCoversBlock();
        bool enter = _settings.Entry switch
        {
            RegionEntry.Never => false,
            RegionEntry.Guard => !covered,
            _ => true,
        };
        return new ProcessingWindow(this, StartPayloadRegion(eligible, enter), start, covered);
    }

    /// <param name="enter">
    /// <c>false</c> when this payload enters no region of its own: a pre-entered region is still taken over and a region
    /// another payload holds is still shared (both are paid for already), and the post-block collection is still
    /// scheduled when the lease ends, but nothing is queued.
    /// </param>
    private IDisposable StartPayloadRegion(bool eligible, bool enter)
    {
        NoGCRegion region = new(this, GCScheduler.MarkGCPaused(), eligible);
        NoGCRegion? stale = null;
        lock (_lock)
        {
            if (_disposed) return region;
            if (!eligible)
            {
                if (_logger.IsDebug) _logger.Debug("No-GC region entry disallowed by strategy.");
                return region;
            }
            Interlocked.Increment(ref _payloadsSinceDecommit);
            if (_region is { IsPreEntry: true } preEntered)
            {
                switch (preEntered.TryHandOver(out long allocated))
                {
                    case PreEntryHandOver.TakenOver:
                        Count(ref Metrics.NoGcRegionPreEntriesTakenOver, ref Metrics.NoGcRegionPreSlotEntriesTakenOver, preEntered.Trigger);
                        CountAllocated(allocated, preEntered.Trigger, takenOver: true);
                        // The payload owns the region now: its lease ends the region, and its own region (never
                        // admitted) carries its collection, exactly as when it admits a region itself.
                        return new SharedRegionLease(preEntered, region, ownerLease: true);
                    case PreEntryHandOver.Stale:
                        Count(ref Metrics.NoGcRegionPreEntriesStale, ref Metrics.NoGcRegionPreSlotEntriesStale, preEntered.Trigger);
                        CountAllocated(allocated, preEntered.Trigger, takenOver: false);
                        // Claimed for retirement; ended below, outside the keeper's lock.
                        stale = preEntered;
                        break;
                    case PreEntryHandOver.Ending:
                        // Let go of by its expiry or a failed entry, but still in the slot: the payload does not
                        // share a region on its way out, it ends it (if nobody has yet) and enters its own.
                        stale = preEntered;
                        break;
                }
            }

            if (stale is null)
            {
                if (!enter && _region is null) return SkipEntry(region);
                if (AdmitLocked(region) is { } held) return held;
                Interlocked.Increment(ref Metrics.NoGcRegionPayloadEntries);
            }
        }

        if (stale is not null)
        {
            // Does nothing when the expiry or a failed entry has let go of it already.
            stale.Release(owner: true);
            // The stale region may have held the scheduler pause and has let go of it now: the payload's region takes
            // it, so the payload holds the pause as it would have without the pre-entry.
            region = new NoGCRegion(this, region.PausedGCScheduler || GCScheduler.MarkGCPaused(), eligible);
            lock (_lock)
            {
                if (_disposed) return region;
                // BENCH: the stale region is ended (or being ended) and nothing new is entered.
                if (!enter) return SkipEntry(region);
                if (ReferenceEquals(_region, stale))
                {
                    // Still being ended, by another thread or by its own entry finishing: the payload takes the slot
                    // now and its entry is queued once that region has left it (see ReleaseRegion), as the runtime
                    // holds one region at a time.
                    stale.Successor = region;
                    _region = region;
                    _pendingEntries++;
                    Interlocked.Increment(ref Metrics.NoGcRegionPayloadEntries);
                    return region;
                }
                if (AdmitLocked(region) is { } held) return held;
                Interlocked.Increment(ref Metrics.NoGcRegionPayloadEntries);
            }
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

    /// <summary>Takes the keeper's slot for <paramref name="region"/>, or returns what the payload holds instead.</summary>
    /// <returns><c>null</c> when the slot was taken and the entry must be queued.</returns>
    private IDisposable? AdmitLocked(NoGCRegion region)
    {
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
        return null;
    }

    /// <summary>BENCH: the payload enters nothing; its lease still holds the scheduler pause and schedules its collection.</summary>
    private NoGCRegion SkipEntry(NoGCRegion region)
    {
        Interlocked.Increment(ref Metrics.NoGcRegionPayloadSkips);
        if (_logger.IsTrace) _logger.Trace($"No-GC region entry skipped ({_settings.Entry}).");
        return region;
    }

    /// <summary>
    /// BENCH: whether the gen0 budget left, as estimated by <see cref="Gen0BudgetTracker"/>, is at least
    /// <see cref="PreEntrySettings.GuardBytes"/>, so the block is expected to run without a gen0 collection.
    /// </summary>
    /// <remarks>An unknown budget never covers the block: the guard then enters, as <c>always</c> does.</remarks>
    private bool BudgetCoversBlock()
    {
        long allocated = _runtime.AllocatedBytes;
        long gcIndex = _runtime.LastGcIndex;
        long budget = _settings.GuardBudgetBytes > 0 ? _settings.GuardBudgetBytes : _runtime.Gen0Budget;
        long left = _budget.EstimateLeft(allocated, gcIndex, budget);
        Volatile.Write(ref Metrics.NoGcRegionGuardGen0BudgetBytes, budget);
        Volatile.Write(ref Metrics.NoGcRegionGuardBudgetLeftBytes, left == Gen0BudgetTracker.Unknown ? -1 : left);
        return left != Gen0BudgetTracker.Unknown && left >= _settings.GuardBytes;
    }

    /// <summary>BENCH: called right after the runtime entered a region for this keeper.</summary>
    private void OnRegionEntered()
    {
        Interlocked.Increment(ref _ownEntries);
        if (_settings.Entry != RegionEntry.Always) _budget.OnRegionEntered(_runtime.AllocatedBytes, _runtime.LastGcIndex);
    }

    /// <summary>BENCH: GC counts and allocation where a payload's processing starts.</summary>
    private readonly record struct ProcessingSample(int Gen0, int Gen1, int Gen2, long OwnEntries, long Allocated);

    private ProcessingSample SampleProcessing()
    {
        // Counts before own entries: an entry finishing in between is then left out of both ends of the window.
        int gen0 = _runtime.CollectionCount(0);
        int gen1 = _runtime.CollectionCount(1);
        int gen2 = _runtime.CollectionCount(2);
        return new(gen0, gen1, gen2, Interlocked.Read(ref _ownEntries), _runtime.AllocatedBytes);
    }

    /// <summary>
    /// BENCH: counts the collections that ran between the payload's start and the end of its lease (the region's end,
    /// or the same point when no region was entered), less this keeper's own region entries in between.
    /// </summary>
    private void EndProcessingWindow(in ProcessingSample start, bool covered)
    {
        ProcessingSample end = SampleProcessing();
        int own = (int)(end.OwnEntries - start.OwnEntries);
        int gen0 = Math.Max(0, end.Gen0 - start.Gen0 - own);
        int gen1 = Math.Max(0, end.Gen1 - start.Gen1 - own);
        int gen2 = Math.Max(0, end.Gen2 - start.Gen2 - own);
        long allocated = Math.Max(0, end.Allocated - start.Allocated);

        Interlocked.Increment(ref Metrics.NoGcRegionPayloadsMeasured);
        Interlocked.Add(ref Metrics.NoGcRegionPayloadAllocatedBytes, allocated);
        Max(ref Metrics.NoGcRegionPayloadAllocatedBytesMax, allocated);
        if (covered) Interlocked.Increment(ref Metrics.NoGcRegionGuardCovered);
        if (gen0 == 0) return;
        Interlocked.Increment(ref Metrics.NoGcRegionPayloadsWithCollectionInProcessing);
        Interlocked.Add(ref Metrics.NoGcRegionGen0CollectionsInProcessing, gen0);
        Interlocked.Add(ref Metrics.NoGcRegionGen1CollectionsInProcessing, gen1);
        Interlocked.Add(ref Metrics.NoGcRegionGen2CollectionsInProcessing, gen2);
        if (covered) Interlocked.Increment(ref Metrics.NoGcRegionGuardMisses);
    }

    /// <summary>BENCH: a payload's lease that closes its processing window before ending the region or lease it wraps.</summary>
    private sealed class ProcessingWindow(GCKeeper keeper, IDisposable lease, ProcessingSample start, bool covered) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                keeper.EndProcessingWindow(start, covered);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    /// <summary>
    /// BENCH: estimates the gen0 allocation budget the runtime has left before its next gen0 collection.
    /// </summary>
    /// <remarks>
    /// <para>left = budget - (allocated now - allocated at the last collection), where the budget is the gen0 budget
    /// summed over the heaps (<see cref="IGcRegionRuntime.Gen0Budget"/>) and a collection is seen as a change of
    /// <see cref="IGcRegionRuntime.LastGcIndex"/>, which region entries do not move. Collections are only seen when
    /// sampled, so the one seen first is assumed to have happened right after the previous sample (the most
    /// allocation it can have missed), except after the keeper's own collection, whose start is known.</para>
    /// <para>A region replaces the budget left with its own per-heap budget and its end does not restore it
    /// (dotnet/runtime v10.0.0 gc.cpp, set_soh_allocations_for_no_gc / restore_data_for_no_gc), so from a region's
    /// entry to the next collection what is left is the region's SOH budget less what was allocated since the entry.</para>
    /// <para>Limits: the runtime collects when one heap's budget runs out; the sum assumes allocation spread evenly
    /// (heap balancing mostly does that, per-heap skew makes the estimate optimistic). Allocated bytes count LOH and
    /// POH too, which draw on their own budgets (pessimistic for gen0), and LOH budget exhaustion triggers a gen2
    /// collection this does not see. The budget is the runtime's current one (it moves with survival) unless pinned
    /// with DOTNET_GCgen0size or overridden with BENCH_GC_REGION_GUARD_BUDGET_MB. Before the first sampled collection
    /// everything allocated since start counts. A background GC in progress is not accounted for.</para>
    /// </remarks>
    internal sealed class Gen0BudgetTracker
    {
        public const long Unknown = long.MinValue;
        // The SOH budget a region of the keeper's size hands every heap, summed (the runtime adds 5%; left out).
        internal static readonly long RegionSohBudget = _defaultSize - _lohSize;
        private readonly Lock _lock = new();
        private long _seenGcIndex = -1;
        private long _lastSampleAllocated;
        private long _allocatedAtGc;
        private bool _regionSinceGc;
        private long _allocatedAtRegionEntry;

        /// <returns>The estimated bytes left (may be negative), or <see cref="Unknown"/> when the budget is unknown.</returns>
        public long EstimateLeft(long allocated, long gcIndex, long budget)
        {
            lock (_lock)
            {
                ObserveLocked(allocated, gcIndex);
                if (budget <= 0) return Unknown;
                return _regionSinceGc
                    ? RegionSohBudget - (allocated - _allocatedAtRegionEntry)
                    : budget - (allocated - _allocatedAtGc);
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

    /// <summary>
    /// Enters the no-GC region ahead of the next payload, so the collection that entry performs runs before
    /// engine_newPayload arrives rather than under it. Called when engine_getBlobs is answered, which precedes the
    /// payload carrying those blobs; repeated calls schedule one entry at a time.
    /// </summary>
    /// <remarks>
    /// Deferred by <see cref="PreEntryDelayMs"/> so the getBlobs answer, which can be several megabytes, is written
    /// before the entry suspends the process; the payload follows the answer by far longer than that. If a payload
    /// arrives first, the pre-entry finds the slot taken and does nothing, as without it. Does nothing while the
    /// strategy disallows the region, so it is on exactly when the payload's own region is.
    /// </remarks>
    public void SchedulePrepareNoGCRegion()
    {
        if ((_settings.Mode & PreEntryMode.GetBlobs) == 0 || _settings.Entry == RegionEntry.Never) return;
        if (!_gcStrategy.CanStartNoGCRegion() || Interlocked.Exchange(ref _preEntryScheduled, 1) != 0) return;
        _ = PrepareAfterDelayAsync();
    }

    private async Task PrepareAfterDelayAsync()
    {
        bool scheduled = true;
        try
        {
            bool elapsed = await _delay(PreEntryDelayMs, _preEntryCts.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            // Cleared before the entry, and only here: a getBlobs answered meanwhile schedules the next one.
            Volatile.Write(ref _preEntryScheduled, 0);
            scheduled = false;
            if (elapsed) PrepareNoGCRegion();
        }
        catch (Exception e)
        {
            if (scheduled) Volatile.Write(ref _preEntryScheduled, 0);
            if (_logger.IsError) _logger.Error("No-GC region pre-entry failed.", e);
        }
    }

    /// <summary>
    /// BENCH: anchors the slot clock on the timestamp of a new head and, while the region is allowed, keeps one loop
    /// that enters the region <see cref="PreEntrySettings.SlotLeadMs"/> before each next slot starts, so blocks without
    /// blobs (no getBlobs call) find it entered too and the entry's pause lands on an idle node.
    /// </summary>
    public void OnNewHead(ulong timestamp)
    {
        if ((_settings.Mode & PreEntryMode.Slot) == 0 || _settings.Entry == RegionEntry.Never || _preEntryCts.IsCancellationRequested) return;
        Volatile.Write(ref _headTimestamp, (long)timestamp);
        if (!_gcStrategy.CanStartNoGCRegion() || Interlocked.Exchange(ref _slotLoopRunning, 1) != 0) return;
        _ = RunSlotLoopAsync();
    }

    private async Task RunSlotLoopAsync()
    {
        try
        {
            // Stops when the region is no longer allowed (e.g. syncing); the next head while it is starts it again.
            while (!_preEntryCts.IsCancellationRequested && _gcStrategy.CanStartNoGCRegion())
            {
                long slotMs = (long)Math.Max(1UL, _gcStrategy.SecondsPerSlot) * 1000;
                long now = _unixTimeMs();
                long slotStart = NextSlotStartMs(Volatile.Read(ref _headTimestamp) * 1000, slotMs, now, _settings.SlotLeadMs);
                // A negative lead fires that long after the slot start.
                long fireAt = slotStart - _settings.SlotLeadMs;
                if (!await _delay((int)Math.Min(int.MaxValue, fireAt - now), _preEntryCts.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding)) return;
                // The slot's block (or a later one) arrived while waiting: nothing to enter ahead of; re-anchored instead.
                if (Volatile.Read(ref _headTimestamp) * 1000 >= slotStart) continue;
                PrepareNoGCRegion(PreEntryTrigger.Slot);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("No-GC region slot pre-entry failed.", e);
        }
        finally
        {
            Volatile.Write(ref _slotLoopRunning, 0);
        }
    }

    /// <summary>The start of the first slot after the head whose fire time (start minus lead) is after <paramref name="nowMs"/>.</summary>
    /// <remarks>Slots follow the head every <paramref name="slotMs"/>, so missed slots are skipped over; a head ahead of
    /// the clock still gives the slot after it.</remarks>
    internal static long NextSlotStartMs(long headMs, long slotMs, long nowMs, long leadMs)
    {
        long target = nowMs + leadMs;
        long slots = target < headMs + slotMs ? 1 : (target - headMs) / slotMs + 1;
        return headMs + slots * slotMs;
    }

    /// <summary>BENCH: bytes the process allocated from a pre-entry's entry to its hand-over (0 while still entering).</summary>
    private static void CountAllocated(long allocated, PreEntryTrigger trigger, bool takenOver)
    {
        bool slot = trigger == PreEntryTrigger.Slot;
        if (takenOver)
        {
            Interlocked.Add(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtTakeover, allocated);
            Max(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtTakeoverMax, allocated);
            if (!slot) return;
            Interlocked.Add(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtTakeover, allocated);
            Max(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtTakeoverMax, allocated);
        }
        else
        {
            Interlocked.Add(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtStale, allocated);
            if (slot) Interlocked.Add(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtStale, allocated);
        }
    }

    private static void Max(ref long max, long value)
    {
        long current = Volatile.Read(ref max);
        while (value > current)
        {
            long seen = Interlocked.CompareExchange(ref max, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private static void Count(ref long all, ref long slot, PreEntryTrigger trigger)
    {
        Interlocked.Increment(ref all);
        if (trigger == PreEntryTrigger.Slot) Interlocked.Increment(ref slot);
    }

    /// <summary>
    /// Enters a no-GC region owned by no payload, which the next payload takes over (see
    /// <see cref="TryStartNoGCRegion"/>) or which ends by itself after <see cref="PreEntryTimeoutMs"/>.
    /// </summary>
    /// <remarks>
    /// Does nothing when the strategy disallows a region, or a region is pending or active. BENCH: nor with
    /// <c>BENCH_GC_REGION_ENTRY=never</c>, nor in guard mode while the gen0 budget left covers a block (the payload
    /// asks the guard again when it arrives).
    /// </remarks>
    /// <returns>Whether an entry was queued.</returns>
    internal bool PrepareNoGCRegion(PreEntryTrigger trigger = PreEntryTrigger.GetBlobs)
    {
        if (!_gcStrategy.CanStartNoGCRegion() || _settings.Entry == RegionEntry.Never) return false;
        if (_settings.Entry == RegionEntry.Guard && BudgetCoversBlock())
        {
            Interlocked.Increment(ref Metrics.NoGcRegionPreEntriesSkippedByGuard);
            return false;
        }
        NoGCRegion region;
        lock (_lock)
        {
            if (_disposed || _region is not null) return false;
            // Nothing to collect after it: a pre-entry that expires unused leaves the post-block collections as they were.
            region = new NoGCRegion(this, GCScheduler.MarkGCPaused(), scheduleGC: false, trigger);
            _region = region;
            _pendingEntries++;
        }

        Count(ref Metrics.NoGcRegionPreEntries, ref Metrics.NoGcRegionPreSlotEntries, trigger);
        try
        {
            _queue(region);
        }
        catch
        {
            Count(ref Metrics.NoGcRegionPreEntriesFailed, ref Metrics.NoGcRegionPreSlotEntriesFailed, trigger);
            region.Dispose();
            CompleteEntry();
            throw;
        }

        _ = ExpirePreEntryAsync(region);
        return true;
    }

    private async Task ExpirePreEntryAsync(NoGCRegion region)
    {
        try
        {
            if (!await _delay(region.TimeoutMs, _preEntryCts.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding)) return;
            if (region.ExpirePreEntry()) Count(ref Metrics.NoGcRegionPreEntriesExpired, ref Metrics.NoGcRegionPreSlotEntriesExpired, region.Trigger);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("No-GC region pre-entry expiry failed.", e);
        }
    }

    /// <summary>What a payload found in a region entered ahead of it.</summary>
    private enum PreEntryHandOver
    {
        /// <summary>The pre-entry let go meanwhile; the region is treated as any other region in the slot.</summary>
        None,
        /// <summary>The payload owns the region, entered or still being entered.</summary>
        TakenOver,
        /// <summary>Too old, too much allocated since entry, or not entered: claimed for the payload to end.</summary>
        Stale,
        /// <summary>Let go of by its expiry or a failed entry while still in the slot: the payload ends it if needed.</summary>
        Ending,
    }

    private void ReleaseRegion(NoGCRegion region)
    {
        NoGCRegion? successor;
        lock (_lock)
        {
            if (ReferenceEquals(_region, region)) _region = null;
            successor = region.Successor;
            region.Successor = null;
            if (_pendingEntries == 0 && _region is null) _entriesDrained?.TrySetResult();
        }

        if (successor is null) return;
        try
        {
            _queue(successor);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("No-GC region entry failed.", e);
            successor.ForceRelease();
            CompleteEntry();
        }
    }

    internal enum PreEntryTrigger
    {
        None,
        GetBlobs,
        Slot,
    }

    /// <summary>BENCH: which triggers enter the region ahead of the payload.</summary>
    [Flags]
    internal enum PreEntryMode
    {
        Off = 0,
        GetBlobs = 1,
        Slot = 2,
        Both = GetBlobs | Slot,
    }

    /// <summary>BENCH: whether engine_newPayload enters the region of its own.</summary>
    internal enum RegionEntry
    {
        /// <summary>Every payload, as before.</summary>
        Always,
        /// <summary>Only while the gen0 budget left is below <see cref="PreEntrySettings.GuardBytes"/>.</summary>
        Guard,
        /// <summary>No payload and no pre-entry; the post-block collection and the decommit stay.</summary>
        Never,
    }

    /// <summary>BENCH: the default for <c>BENCH_GC_REGION_GUARD_MB</c>, i.e. the allocation a block is allowed.</summary>
    internal const long DefaultGuardBytes = 256_000_000;

    /// <summary>BENCH knobs: <c>BENCH_GC_PREENTRY_MODE</c> (off, getblobs, slot, both), <c>BENCH_GC_PRESLOT_LEAD_MS</c>,
    /// <c>BENCH_GC_PRESLOT_EXPIRY_MS</c>, <c>BENCH_GC_PREENTRY_MAX_ALLOC_MB</c>, <c>BENCH_GC_REGION_ENTRY</c> (always,
    /// guard, never), <c>BENCH_GC_REGION_GUARD_MB</c>, <c>BENCH_GC_REGION_GUARD_BUDGET_MB</c> (0: the runtime's),
    /// <c>BENCH_GC_RECOMMIT_AFTER_DECOMMIT</c> (1/true); unset or malformed ones take defaults.</summary>
    /// <remarks>
    /// Pre-entries (getblobs, slot) are made with <c>always</c> as before, never with <c>never</c>, and with
    /// <c>guard</c> only when the guard would enter at that moment; a payload always takes over a pre-entered region.
    /// </remarks>
    internal sealed record PreEntrySettings(PreEntryMode Mode, int SlotLeadMs, int SlotExpiryMs, long MaxAllocatedBytes,
        RegionEntry Entry = RegionEntry.Always, long GuardBytes = DefaultGuardBytes, long GuardBudgetBytes = 0,
        bool RecommitAfterDecommit = false)
    {
        public static PreEntrySettings Default { get; } = new(PreEntryMode.Both, 1_000, 6_000, PreEntryMaxAllocatedBytes);

        public static PreEntrySettings FromEnvironment() => Parse(
            Environment.GetEnvironmentVariable("BENCH_GC_PREENTRY_MODE"),
            Environment.GetEnvironmentVariable("BENCH_GC_PRESLOT_LEAD_MS"),
            Environment.GetEnvironmentVariable("BENCH_GC_PRESLOT_EXPIRY_MS"),
            Environment.GetEnvironmentVariable("BENCH_GC_PREENTRY_MAX_ALLOC_MB"),
            Environment.GetEnvironmentVariable("BENCH_GC_REGION_ENTRY"),
            Environment.GetEnvironmentVariable("BENCH_GC_REGION_GUARD_MB"),
            Environment.GetEnvironmentVariable("BENCH_GC_REGION_GUARD_BUDGET_MB"),
            Environment.GetEnvironmentVariable("BENCH_GC_RECOMMIT_AFTER_DECOMMIT"));

        public static PreEntrySettings Parse(string? mode, string? leadMs, string? expiryMs, string? maxAllocMb,
            string? entry = null, string? guardMb = null, string? guardBudgetMb = null, string? recommit = null) => new(
            mode?.Trim().ToLowerInvariant() switch
            {
                "off" => PreEntryMode.Off,
                "getblobs" => PreEntryMode.GetBlobs,
                "slot" => PreEntryMode.Slot,
                _ => PreEntryMode.Both,
            },
            // Negative: after the slot start.
            int.TryParse(leadMs, out int lead) ? lead : Default.SlotLeadMs,
            int.TryParse(expiryMs, out int expiry) && expiry > 0 ? expiry : Default.SlotExpiryMs,
            long.TryParse(maxAllocMb, out long mb) && mb >= 0 ? mb.MB : Default.MaxAllocatedBytes,
            entry?.Trim().ToLowerInvariant() switch
            {
                "guard" => RegionEntry.Guard,
                "never" => RegionEntry.Never,
                _ => RegionEntry.Always,
            },
            long.TryParse(guardMb, out long guard) && guard >= 0 ? guard.MB : Default.GuardBytes,
            long.TryParse(guardBudgetMb, out long budget) && budget > 0 ? budget.MB : 0,
            recommit?.Trim().ToLowerInvariant() is "1" or "true");

        public override string ToString() =>
            $"mode {Mode}, slot lead {SlotLeadMs} ms (negative: after the slot start), slot expiry {SlotExpiryMs} ms, takeover allocation cap {MaxAllocatedBytes / 1.MB} MB, " +
            $"payload entry {Entry}, guard {GuardBytes / 1.MB} MB, guard budget {(GuardBudgetBytes > 0 ? $"{GuardBudgetBytes / 1.MB} MB" : "runtime's")}, re-commit after decommit {RecommitAfterDecommit}";
    }

    private sealed class NoGCRegion(GCKeeper keeper, bool pausedGCScheduler, bool scheduleGC, PreEntryTrigger trigger = PreEntryTrigger.None)
        : IDisposable, IThreadPoolWorkItem
    {
        private readonly Lock _stateLock = new();
        private bool _released;
        private bool _starting;
        private const int MaxLeases = 2;
        private bool _active;
        private int _leases = 1;
        private bool _ownerReleased;
        // Entered ahead of the payload: the lease is held by no payload until one takes it over or it expires.
        private readonly bool _isPreEntry = trigger != PreEntryTrigger.None;
        private bool _preEntryOwned = trigger != PreEntryTrigger.None;
        private bool _entered;
        private readonly long _createdTimestamp = trigger != PreEntryTrigger.None ? keeper._timestamp() : 0;

        public PreEntryTrigger Trigger { get; } = trigger;

        /// <summary>How long a pre-entered region waits for its payload: a slot's covers the lead and the slot's start.</summary>
        public int TimeoutMs => Trigger == PreEntryTrigger.Slot ? keeper._settings.SlotExpiryMs : PreEntryTimeoutMs;
        private long _allocatedAtEntry;
        private bool _takenOver;

        public bool PausedGCScheduler => pausedGCScheduler;

        public bool IsPreEntry => _isPreEntry;

        /// <summary>A payload's region waiting, under the keeper's lock, for this region to leave the slot.</summary>
        public NoGCRegion? Successor { get; set; }

        /// <summary>
        /// Hands a region entered ahead of the payload to that payload while it still has the payload's budget;
        /// otherwise claims it for retirement, which the caller completes with <c>Release(owner: true)</c>.
        /// </summary>
        public PreEntryHandOver TryHandOver(out long allocated)
        {
            allocated = 0;
            lock (_stateLock)
            {
                // Taken over by an earlier payload: shared like any payload's region.
                if (_takenOver) return PreEntryHandOver.None;
                if (!_preEntryOwned || _released || _ownerReleased) return PreEntryHandOver.Ending;
                _preEntryOwned = false;
                if (_active) allocated = keeper._runtime.AllocatedBytes - _allocatedAtEntry;
                bool usable = Stopwatch.GetElapsedTime(_createdTimestamp, keeper._timestamp()).TotalMilliseconds < TimeoutMs
                    && (_active
                        ? keeper._runtime.IsActive && allocated < keeper._settings.MaxAllocatedBytes
                        // Still queued or being entered: the payload takes the entry over as if it had queued it.
                        : !_entered);
                if (usable)
                {
                    _takenOver = true;
                    return PreEntryHandOver.TakenOver;
                }

                // Takes no lease meanwhile (TryAddLease checks the owner).
                _ownerReleased = true;
                return PreEntryHandOver.Stale;
            }
        }

        /// <summary>Ends a region entered ahead of a payload that no payload took over.</summary>
        /// <returns>Whether this call let go of it (it was neither taken over nor released before).</returns>
        public bool ExpirePreEntry()
        {
            lock (_stateLock)
            {
                if (!_preEntryOwned || _released) return false;
                _preEntryOwned = false;
                _ownerReleased = true;
            }

            Release(owner: true);
            return true;
        }

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
                // BENCH: waits for a re-commit's throwaway region to end (see RecommitAfterDecommit).
                lock (keeper._runtimeLock)
                {
                    started = keeper._runtime.TryStart(_defaultSize, _lohSize);
                }
                if (started) keeper.OnRegionEntered();
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
                _entered = true;
                if (started && !_released)
                {
                    _active = true;
                    if (_isPreEntry) _allocatedAtEntry = keeper._runtime.AllocatedBytes;
                    return;
                }
            }

            if (started) EndRegion();
            else keeper.ReleaseRegion(this);
            // A failed pre-entry nobody took over lets go of the scheduler pause now rather than at its expiry.
            if (!started && _isPreEntry && ExpirePreEntry())
                Count(ref Metrics.NoGcRegionPreEntriesFailed, ref Metrics.NoGcRegionPreSlotEntriesFailed, Trigger);
        }

        /// <summary>Released by the payload that admitted the region, after which it takes no new leases.</summary>
        public void Dispose() => Release(owner: true);

        /// <summary>Ends the region whatever is still leased, for the keeper's own shutdown.</summary>
        public void ForceRelease() => Release(force: true);

        public void Release(bool owner = false, bool force = false)
        {
            bool end;
            bool release;
            bool byPayload;
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
                // The last payload in it lets go (not an unused pre-entry's expiry, nor shutdown).
                byPayload = !force && (!_isPreEntry || _takenOver);
            }

            if (pausedGCScheduler) GCScheduler.MarkGCResumed();
            if (end) EndRegion(byPayload);
            else if (release) keeper.ReleaseRegion(this);
            if (scheduleGC) keeper.ScheduleGC();
        }

        private void EndRegion(bool byPayload = false)
        {
            try
            {
                if (keeper._runtime.IsActive)
                {
                    keeper._runtime.End();
                }
                else if (byPayload)
                {
                    // The runtime left the region under the payload, its budget spent before the payload was done.
                    Interlocked.Increment(ref Metrics.NoGcRegionEndedByRuntime);
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

    /// <summary>One overlapping payload's hold on a region another payload admitted, or that it took over from a pre-entry.</summary>
    /// <param name="shared">The region covering this payload, released when the last payload in it is done.</param>
    /// <param name="own">This payload's own region, never admitted, carrying its scheduler pause and its collection.</param>
    /// <param name="ownerLease">The payload took the region over from a pre-entry and holds it as its owner.</param>
    private sealed class SharedRegionLease(NoGCRegion shared, NoGCRegion own, bool ownerLease = false) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            shared.Release(owner: ownerLease);
            own.Dispose();
        }
    }

    private long? _lastGcTimeMs;

    /// <summary>
    /// BENCH: after the aggressive collection has decommitted every free region, enters and at once ends a throwaway
    /// region, so the regions the next payload's entry needs are committed here, off the payload's path, rather than
    /// inside that entry's suspension (2.0-2.5 ms instead of ~0.2 ms on the entry after each decommit).
    /// </summary>
    /// <remarks>
    /// <para>dotnet/runtime v10.0.0 gc.cpp: the entry links and commits (mprotect, no touch, no zeroing) gen0 regions
    /// for the whole budget in extend_soh_for_no_gc; ending it leaves them linked and committed, and nothing decommits
    /// them before the next collection (decommit_step only takes regions a collection queued). So the next entry finds
    /// them committed, and RSS does not grow until they are allocated in. The decommit still returns everything
    /// else.</para>
    /// <para>Costs: one more suspension (the slow entry, moved), one more count in GC.CollectionCount(0..2), and,
    /// until the next collection, the region's budget in place of the runtime's own (as after every region).</para>
    /// <para>Skipped when the next payload has cancelled the pending collection meanwhile, a region is held or
    /// active, or the keeper is stopping. The runtime lock keeps a payload's entry from starting inside it.</para>
    /// </remarks>
    private void RecommitAfterDecommit(CancellationTokenSource pendingGcCts)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
            if (pendingGcCts.IsCancellationRequested || _disposed || _region is not null || _runtime.IsActive)
            {
                Interlocked.Increment(ref Metrics.NoGcRegionRecommitsSkipped);
                return;
            }
            // Taken under the keeper's lock: a payload admitted after this check queues its entry, which waits here.
            _runtimeLock.Enter();
        }

        try
        {
            bool started = false;
            try
            {
                started = _runtime.TryStart(_defaultSize, _lohSize);
                if (started) OnRegionEntered();
            }
            finally
            {
                if (started && _runtime.IsActive) _runtime.End();
            }

            if (started) Interlocked.Increment(ref Metrics.NoGcRegionRecommits);
            else Interlocked.Increment(ref Metrics.NoGcRegionRecommitsSkipped);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            Interlocked.Increment(ref Metrics.NoGcRegionRecommitsSkipped);
            if (_logger.IsDebug) _logger.Debug($"No-GC region re-commit failed: {e.Message}");
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

                        // BENCH: a re-commit after the decommit stays cancellable by the next payload until it starts.
                        if (ReferenceEquals(_pendingGcCts, pendingGcCts) && !(decommit && _settings.RecommitAfterDecommit)) _pendingGcCts = null;
                    }

                    if (_logger.IsDebug) _logger.Debug($"Forcing GC collection of gen {generation}, compacting {compacting}");
                    long allocatedBefore = _runtime.AllocatedBytes;
                    bool collected = _runtime.Collect(generation, mode, compacting);
                    if (collected && _settings.Entry != RegionEntry.Always)
                    {
                        _budget.OnCollected(allocatedBefore, _runtime.AllocatedBytes, _runtime.LastGcIndex);
                    }
                    if (collected && decommit)
                    {
                        Interlocked.Add(ref _payloadsSinceDecommit, -payloadsSinceDecommit);
                        if (_settings.RecommitAfterDecommit) RecommitAfterDecommit(pendingGcCts);
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
