// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

public unsafe interface IArenaManager : IDisposable
{
    void Initialize(IReadOnlyList<CatalogEntry> entries);

    /// <summary>
    /// Create an <see cref="ArenaWriter"/> for a new snapshot slice.
    /// </summary>
    /// <param name="estimatedSize">Estimated byte size of the slice; drives the shared-vs-dedicated arena choice.</param>
    /// <param name="small">
    /// <c>true</c> for sub-CompactSize snapshots (<c>PersistedBase</c> / <c>PersistedSmallCompacted</c>),
    /// which are packed into their own arena files separate from the larger tiers.
    /// </param>
    ArenaWriter CreateWriter(long estimatedSize, bool small = false);
    ArenaReservation Open(in SnapshotLocation location);

    /// <summary>
    /// Drop <paramref name="deadSize"/> bytes of <paramref name="file"/> as dead. The caller
    /// (typically <see cref="ArenaReservation.CleanUp"/>) handles file-side <c>madvise</c> /
    /// <c>posix_fadvise</c> and tracker-forget itself, so this method only does the atomic
    /// set/dict/metric bookkeeping that needs the manager's lock.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the file survives in the manager (still has live data); <c>false</c> if
    /// this call removed it (all bytes dead) or the manager is shutting down. Callers use this
    /// to skip disk reclamation on a file that is about to be deleted or preserved.
    /// </returns>
    bool MarkDead(ArenaFile file, long deadSize);

    /// <summary>
    /// Punch a hole over the <c>[offset, offset + size)</c> range of <paramref name="file"/>
    /// to free its disk blocks, when both the operator config flag and the adaptive
    /// per-manager support flag allow it. The adaptive flag latches off permanently after
    /// the first filesystem-unsupported error. No-op for implementations without on-disk arenas.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the range was actually hole-punched — the kernel has invalidated its
    /// page cache, so the caller can skip a follow-up <c>posix_fadvise(DONTNEED)</c>;
    /// <c>false</c> if punch-hole was skipped (config / adaptive flag) or failed.
    /// </returns>
    bool TryPunchHole(ArenaFile file, long offset, long size);

    /// <summary>
    /// Drop tracker entries for every fully-covered OS page in
    /// <c>[byteOffset, byteOffset + byteSize)</c> of <paramref name="arenaId"/>. The page-
    /// rounding mirrors <see cref="ArenaFile.AdviseDontNeed"/> (offset rounded up, end rounded
    /// down) so the tracker drops the same pages the kernel was just told to forget. No-op for
    /// implementations that disable the tracker.
    /// </summary>
    void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize);

    /// <summary>
    /// Record a per-page access against the residency clock. With <paramref name="inline"/> <c>false</c>
    /// (the generic read path) the access is packed onto a bounded ring that a single background worker
    /// drains, running the clock (<see cref="PageResidencyTracker.TryTouch"/>) off the reader thread and
    /// issuing <c>madvise(MADV_DONTNEED)</c> for any page it displaces; the return value is unspecified
    /// (the outcome is computed later). With <paramref name="inline"/> <c>true</c> the clock runs
    /// synchronously on the calling thread, dispatching any displaced page inline, and the real
    /// <see cref="PageResidencyTracker.TouchOutcome"/> is returned.
    /// </summary>
    /// <remarks>
    /// The inline form is used by the deliberate pre-fault path
    /// (<see cref="ArenaReservation.TouchRangePopulate"/>), which needs the synchronous
    /// non-<see cref="PageResidencyTracker.TouchOutcome.Hit"/> count to decide a single batched
    /// <c>MADV_POPULATE_READ</c>, and as the ring-full fallback of the background form. Returns
    /// <see cref="PageResidencyTracker.TouchOutcome.Hit"/> when the tracker is disabled. Implementations
    /// with no per-page mapping (the in-memory test arena) may record both forms synchronously.
    /// <paramref name="pageIdx"/> is the arena-absolute page index (<c>offset / Environment.SystemPageSize</c>).
    /// </remarks>
    PageResidencyTracker.TouchOutcome Touch(int arenaId, int pageIdx, bool inline);

    /// <summary>
    /// Per-arena page residency tracker. Reservations record per-page accesses through
    /// <see cref="Touch"/> rather than touching the tracker directly; the manager owns the tracker and
    /// disposes it. Instances configured with zero cache bytes
    /// (<c>PersistedSnapshotArenaPageCacheBytes = 0</c>, as in tests) return a 0-capacity tracker whose
    /// <c>TryTouch</c> is a no-op.
    /// </summary>
    PageResidencyTracker PageTracker { get; }
}
