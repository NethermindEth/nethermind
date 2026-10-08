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
                switch (preEntered.TryHandOver())
                {
                    case PreEntryHandOver.TakenOver:
                        Count(ref Metrics.NoGcRegionPreEntriesTakenOver, ref Metrics.NoGcRegionPreSlotEntriesTakenOver, preEntered.Trigger);
                        // The payload owns the region now: its lease ends the region, and its own region (never
                        // admitted) carries its collection, exactly as when it admits a region itself.
                        return new SharedRegionLease(preEntered, region, ownerLease: true);
                    case PreEntryHandOver.Stale:
                        Count(ref Metrics.NoGcRegionPreEntriesStale, ref Metrics.NoGcRegionPreSlotEntriesStale, preEntered.Trigger);
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

            if (stale is null && AdmitLocked(region) is { } held) return held;
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
                if (ReferenceEquals(_region, stale))
                {
                    // Still being ended, by another thread or by its own entry finishing: the payload takes the slot
                    // now and its entry is queued once that region has left it (see ReleaseRegion), as the runtime
                    // holds one region at a time.
                    stale.Successor = region;
                    _region = region;
                    _pendingEntries++;
                    return region;
                }
                if (AdmitLocked(region) is { } held) return held;
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
        if ((_settings.Mode & PreEntryMode.GetBlobs) == 0) return;
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
        if ((_settings.Mode & PreEntryMode.Slot) == 0 || _preEntryCts.IsCancellationRequested) return;
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
                long fireAt = NextSlotStartMs(Volatile.Read(ref _headTimestamp) * 1000, slotMs, now, _settings.SlotLeadMs) - _settings.SlotLeadMs;
                if (!await _delay((int)Math.Min(int.MaxValue, fireAt - now), _preEntryCts.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding)) return;
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

    /// <summary>The start of the first slot after the head whose lead has not begun at <paramref name="nowMs"/>.</summary>
    /// <remarks>Slots follow the head every <paramref name="slotMs"/>, so missed slots are skipped over; a head ahead of
    /// the clock still gives the slot after it.</remarks>
    internal static long NextSlotStartMs(long headMs, long slotMs, long nowMs, long leadMs)
    {
        long target = nowMs + leadMs;
        long slots = target < headMs + slotMs ? 1 : (target - headMs) / slotMs + 1;
        return headMs + slots * slotMs;
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
    /// <remarks>Does nothing when the strategy disallows a region, or a region is pending or active.</remarks>
    /// <returns>Whether an entry was queued.</returns>
    internal bool PrepareNoGCRegion(PreEntryTrigger trigger = PreEntryTrigger.GetBlobs)
    {
        if (!_gcStrategy.CanStartNoGCRegion()) return false;
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

    /// <summary>BENCH knobs: <c>BENCH_GC_PREENTRY_MODE</c> (off, getblobs, slot, both), <c>BENCH_GC_PRESLOT_LEAD_MS</c>,
    /// <c>BENCH_GC_PRESLOT_EXPIRY_MS</c>, <c>BENCH_GC_PREENTRY_MAX_ALLOC_MB</c>; unset or malformed ones take defaults.</summary>
    internal sealed record PreEntrySettings(PreEntryMode Mode, int SlotLeadMs, int SlotExpiryMs, long MaxAllocatedBytes)
    {
        public static PreEntrySettings Default { get; } = new(PreEntryMode.Both, 1_000, 6_000, PreEntryMaxAllocatedBytes);

        public static PreEntrySettings FromEnvironment() => Parse(
            Environment.GetEnvironmentVariable("BENCH_GC_PREENTRY_MODE"),
            Environment.GetEnvironmentVariable("BENCH_GC_PRESLOT_LEAD_MS"),
            Environment.GetEnvironmentVariable("BENCH_GC_PRESLOT_EXPIRY_MS"),
            Environment.GetEnvironmentVariable("BENCH_GC_PREENTRY_MAX_ALLOC_MB"));

        public static PreEntrySettings Parse(string? mode, string? leadMs, string? expiryMs, string? maxAllocMb) => new(
            mode?.Trim().ToLowerInvariant() switch
            {
                "off" => PreEntryMode.Off,
                "getblobs" => PreEntryMode.GetBlobs,
                "slot" => PreEntryMode.Slot,
                _ => PreEntryMode.Both,
            },
            int.TryParse(leadMs, out int lead) && lead >= 0 ? lead : Default.SlotLeadMs,
            int.TryParse(expiryMs, out int expiry) && expiry > 0 ? expiry : Default.SlotExpiryMs,
            long.TryParse(maxAllocMb, out long mb) && mb >= 0 ? mb.MB : Default.MaxAllocatedBytes);

        public override string ToString() =>
            $"mode {Mode}, slot lead {SlotLeadMs} ms, slot expiry {SlotExpiryMs} ms, takeover allocation cap {MaxAllocatedBytes / 1.MB} MB";
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
        public PreEntryHandOver TryHandOver()
        {
            lock (_stateLock)
            {
                // Taken over by an earlier payload: shared like any payload's region.
                if (_takenOver) return PreEntryHandOver.None;
                if (!_preEntryOwned || _released || _ownerReleased) return PreEntryHandOver.Ending;
                _preEntryOwned = false;
                bool usable = Stopwatch.GetElapsedTime(_createdTimestamp, keeper._timestamp()).TotalMilliseconds < TimeoutMs
                    && (_active
                        ? keeper._runtime.IsActive && keeper._runtime.AllocatedBytes - _allocatedAtEntry < keeper._settings.MaxAllocatedBytes
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

                        if (ReferenceEquals(_pendingGcCts, pendingGcCts)) _pendingGcCts = null;
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
