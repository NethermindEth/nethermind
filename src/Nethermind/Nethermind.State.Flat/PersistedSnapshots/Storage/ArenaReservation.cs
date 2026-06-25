// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// A reservation of space within an arena. Owns a lease on its <see cref="ArenaFile"/> and
/// coordinates lifecycle (eviction, punch-hole, tracker bookkeeping) with the owning
/// <see cref="IArenaManager"/> on disposal.
/// </summary>
public sealed class ArenaReservation : SmallRefCountingDisposable
{
    private readonly IArenaManager _arenaManager;
    // The owning file. Held directly so read-path operations skip the manager's id →
    // ArenaFile dictionary lookup.
    private readonly ArenaFile _arenaFile;
    private readonly long _initialSize;

    private int ArenaId { get; }
    internal long Offset { get; }
    public long Size { get; internal set; }
    // Set once via PersistOnShutdown; checked in CleanUp to skip the punch-hole reclaim
    // so a snapshot the next session needs to rehydrate is not zeroed on disk. Independent
    // of the file-level _preserveOnDispose: a shared arena may still hold other live
    // reservations, so the file stays alive regardless — only the punch over THIS
    // reservation's range needs to be suppressed.
    private int _preserveOnDispose;

    /// <summary>
    /// On-disk byte footprint of this reservation, page-padded up to where the next
    /// reservation begins. For a shared arena <see cref="Offset"/> is OS-page-aligned and
    /// the next reservation starts at <c>Offset + Footprint</c>, so reclamation syscalls
    /// (<c>madvise</c> / <c>posix_fadvise</c> / <c>fallocate(PUNCH_HOLE)</c>) over
    /// <c>[Offset, Offset + Footprint)</c> cover whole pages exactly without touching a
    /// neighbour. Capped at the file so a truncated dedicated arena reduces to <see cref="Size"/>.
    /// </summary>
    private long Footprint => Math.Min(PageLayout.RoundUpToOsPage(Size), _arenaFile.MappedSize - Offset);

    public ArenaReservation(IArenaManager arenaManager, ArenaFile arenaFile,
                            int arenaId, long offset, long size)
        : base(1)
    {
        // Pin the arena file so it can't be torn down while this reservation is alive.
        // TryAcquireLease handles the race where the manager removed the file from its
        // dict between the caller's lookup and this ctor — surface as InvalidOperationException
        // so the caller's lease path can react instead of operating on a doomed file.
        if (!arenaFile.TryAcquireLease())
            throw new InvalidOperationException(
                $"Cannot construct ArenaReservation for arena {arenaId}: the underlying ArenaFile is already being disposed.");
        _arenaManager = arenaManager;
        _arenaFile = arenaFile;
        ArenaId = arenaId;
        Offset = offset;
        Size = size;
        _initialSize = size;
        Interlocked.Increment(ref Metrics._arenaReservationCount);
        Interlocked.Add(ref Metrics._arenaReservationBytes, size);
    }

    /// <summary>
    /// Reader pin/getspan hook: probe every OS page overlapping the reader-relative byte range
    /// <c>[localOffset, localOffset + length)</c> against the <see cref="PageResidencyTracker"/> and queue
    /// any displaced occupant for the background <c>madvise(MADV_DONTNEED)</c> advisor. Records residency
    /// only — it does <em>not</em> pre-fault; the reader takes the inline minor fault on first access.
    /// </summary>
    internal void TouchRange(long localOffset, long length)
    {
        if (length <= 0) return;
        TrackPages(localOffset, length, out _, out _);
    }

    /// <summary>
    /// Deliberate pre-fault: track residency as <see cref="TouchRange"/> does, then — when more than one
    /// probed page was a non-<see cref="PageResidencyTracker.TouchOutcome.Hit"/> — issue a <em>single</em>
    /// <c>madvise(MADV_POPULATE_READ)</c> over the page-aligned envelope of the range. Used to warm a known
    /// hot region up front (e.g. a freshly-written index block on compaction), not from the generic read path.
    /// </summary>
    /// <remarks>
    /// Coalesces the per-page pre-fault syscalls into one for a contiguous read. <c>MADV_POPULATE_READ</c> is
    /// a no-op on already-resident pages, so over-faulting the few hot pages inside the range is harmless.
    /// When only a single probed page is cold the batched <c>madvise</c> is skipped — a one-page syscall is
    /// not amortized vs. the inline minor fault the reader would otherwise take.
    /// </remarks>
    internal void TouchRangePopulate(long localOffset, long length)
    {
        if (length <= 0) return;
        int missedCount = TrackPages(localOffset, length, out long firstPageBase, out long lastPageBaseExclusive);
        if (missedCount > 1)
            _arenaFile.PopulateRead(firstPageBase, lastPageBaseExclusive - firstPageBase);
    }

    /// <summary>
    /// Probe every OS page overlapping <c>[localOffset, localOffset + length)</c> against the
    /// <see cref="PageResidencyTracker"/>, queuing any displaced occupant for the background
    /// <c>madvise(MADV_DONTNEED)</c> advisor. Returns the count of probed pages that were not a
    /// <see cref="PageResidencyTracker.TouchOutcome.Hit"/> and, via the out parameters, the page-aligned
    /// envelope of the range (for a caller that then pre-faults it).
    /// </summary>
    private int TrackPages(long localOffset, long length, out long firstPageBase, out long lastPageBaseExclusive)
    {
        int pageSize = Environment.SystemPageSize;
        long absStart = Offset + localOffset;
        long absEnd = absStart + length;
        firstPageBase = absStart & ~(long)(pageSize - 1);
        lastPageBaseExclusive = (absEnd + pageSize - 1) & ~(long)(pageSize - 1);
        int firstPage = (int)(firstPageBase / pageSize);
        int lastPage = (int)((lastPageBaseExclusive - 1) / pageSize);

        int missedCount = 0;
        PageResidencyTracker tracker = _arenaManager.PageTracker;
        for (int p = firstPage; p <= lastPage; p++)
        {
            PageResidencyTracker.TouchOutcome outcome = tracker.TryTouch(ArenaId, p,
                out int evictedArenaId, out int evictedPageIdx);
            if (outcome == PageResidencyTracker.TouchOutcome.Hit) continue;
            missedCount++;
            if (outcome == PageResidencyTracker.TouchOutcome.Evicted)
                _arenaManager.QueueEviction(evictedArenaId, evictedPageIdx);
        }
        return missedCount;
    }

    /// <summary>
    /// Begin a scoped whole-buffer read. The returned session holds a lease on this
    /// reservation; disposing it releases the lease and (by default) issues
    /// <c>madvise(MADV_DONTNEED)</c> on the mapped range. Pass
    /// <paramref name="adviseDontNeedOnDispose"/> = <c>false</c> when the caller has
    /// arranged an explicit eviction elsewhere and a redundant madvise on session close
    /// would be wasteful.
    /// </summary>
    public WholeReadSession BeginWholeReadSession(bool adviseDontNeedOnDispose = true) =>
        new(this, adviseDontNeedOnDispose);

    internal ArenaFile.MmapWholeView OpenWholeView(bool adviseDontNeedOnDispose) =>
        _arenaFile.OpenWholeView(Offset, Size, adviseDontNeedOnDispose);

    /// <summary>
    /// Construct an <see cref="ArenaByteReader"/> over this reservation's bytes. The reader
    /// reports each read/pin to the arena's <see cref="PageResidencyTracker"/> so collision-displaced
    /// OS pages can be advised <c>MADV_DONTNEED</c> on eviction. Pointer-backed so &gt;2 GiB
    /// reservations are addressable.
    /// </summary>
    public unsafe ArenaByteReader CreateReader() =>
        new(_arenaFile.BasePtr + Offset, Size, this);

    public void AdviseDontNeed()
    {
        long footprint = Footprint;
        _arenaFile.AdviseDontNeed(Offset, footprint);
        _arenaManager.ForgetTrackerRange(ArenaId, Offset, footprint);
    }

    /// <summary>
    /// Forget every PageResidencyTracker entry that points into this reservation. Skips the
    /// <c>madvise(MADV_DONTNEED)</c> step that <see cref="AdviseDontNeed"/> does; use this
    /// when the page-cache side has already been advised away (e.g. by a freshly-closed
    /// <see cref="WholeReadSession"/> over the same range) and only the tracker needs cleaning.
    /// </summary>
    public void ForgetTracker() =>
        _arenaManager.ForgetTrackerRange(ArenaId, Offset, Footprint);

    /// <summary>
    /// Demote variant of <see cref="AdviseDontNeed"/>: <c>madvise(MADV_DONTNEED)</c> plus
    /// <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> over the reservation's range, then the
    /// matching tracker-forget. Drops both the mmap working set and the OS file-cache pages
    /// without freeing disk blocks — unlike <see cref="CleanUp"/> it must not punch a hole,
    /// because the owning snapshot stays alive and readable.
    /// </summary>
    public void AdviseAndFadviseDontNeed()
    {
        long footprint = Footprint;
        _arenaFile.AdviseDontNeed(Offset, footprint);
        _arenaFile.FadviseDontNeed(Offset, footprint);
        _arenaManager.ForgetTrackerRange(ArenaId, Offset, footprint);
    }

    /// <summary>
    /// <c>fsync(2)</c> the underlying <see cref="ArenaFile"/>. Called by the convert/compact
    /// paths after the writer's <c>Complete</c> so the freshly-written metadata is durable
    /// on disk before the catalog records this reservation.
    /// </summary>
    public void Fsync() => _arenaFile.Fsync();

    /// <summary>
    /// Mark this reservation AND its underlying <see cref="ArenaFile"/> for shutdown-survival.
    /// Called by <see cref="PersistedSnapshots.PersistedSnapshot.PersistOnShutdown"/> as the
    /// snapshot is being marked for survival across the next session. The reservation-level
    /// flag suppresses the punch-hole reclaim in <see cref="CleanUp"/>; the file-level flag
    /// (set by the forwarded call) suppresses <c>File.Delete</c> in <see cref="ArenaFile.CleanUp"/>.
    /// </summary>
    public void PersistOnShutdown()
    {
        Interlocked.Exchange(ref _preserveOnDispose, 1);
        _arenaFile.PersistOnShutdown();
    }

    protected override void CleanUp()
    {
        // File-side ops on the ref we already hold — no manager dict lookup. MarkDead does
        // the atomic set/dict/metric bookkeeping; the page-padded Footprint keeps its
        // DeadBytes >= Frontier accounting exact for shared arenas.
        long footprint = Footprint;
        _arenaFile.AdviseDontNeed(Offset, footprint);
        bool fileSurvives = _arenaManager.MarkDead(_arenaFile, footprint);
        // A reservation flagged PersistOnShutdown must not be punched even when the file
        // survives — the next session needs to mmap this exact range. A file MarkDead removed
        // is about to be File.Delete'd — punching it is wasted work. A successful punch-hole
        // already invalidates the page cache, so the follow-up fadvise is then redundant and
        // skipped.
        bool preserve = Volatile.Read(ref _preserveOnDispose) == 1;
        bool punched = !preserve && fileSurvives && _arenaManager.TryPunchHole(_arenaFile, Offset, footprint);
        // Skip the fadvise when the file did not survive — it is about to be deleted on the last lease
        // release below, which drops its pages anyway.
        if (!punched && fileSurvives)
            _arenaFile.FadviseDontNeed(Offset, footprint);
        _arenaManager.ForgetTrackerRange(ArenaId, Offset, footprint);
        Interlocked.Decrement(ref Metrics._arenaReservationCount);
        Interlocked.Add(ref Metrics._arenaReservationBytes, -_initialSize);
        _arenaFile.Dispose();
    }
}
