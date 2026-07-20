// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

public unsafe interface IArenaManager : IDisposable
{
    /// <summary>
    /// Reconcile the durable catalog against the slices this manager can back and return the loadable
    /// subset. On-disk managers back every catalogued entry (returning it unchanged; a missing file is
    /// surfaced later on <see cref="Open"/>); a session-ephemeral RAM manager backs nothing across a
    /// restart and returns an empty set so the loader skips — and purges — the orphaned catalog rows.
    /// </summary>
    IReadOnlyList<CatalogEntry> Initialize(IReadOnlyList<CatalogEntry> entries);

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
    /// Post-<see cref="ArenaWriter.Complete"/> bookkeeping: publish the file's new
    /// <paramref name="newFrontier"/> and (for a shared arena with room left,
    /// <paramref name="hasHeadroom"/>) return it to the writable pool. Called by the writer, not the
    /// application.
    /// </summary>
    void OnWriteCompleted(ArenaFile file, long newFrontier, bool hasHeadroom);

    /// <summary>Bookkeeping after a cancelled write on a shared (non-dedicated) arena.</summary>
    void OnWriteCancelledShared(ArenaFile file);

    /// <summary>Bookkeeping after a cancelled write on a dedicated arena (the writer already dropped its ref).</summary>
    void OnWriteCancelledDedicated(ArenaFile file);

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
    /// Enqueue a page eviction for asynchronous dispatch. The implementation pushes
    /// <c>(arenaId, pageIdx)</c> onto a bounded MPSC ring drained by a background worker that
    /// performs the <c>madvise(MADV_DONTNEED)</c> syscall
    /// off the producer thread. The drain re-checks <see cref="PageResidencyTracker.ContainsPage"/>
    /// and skips the syscall if the page returned to the working set in the meantime. On
    /// ring-full the producer falls back to inline dispatch so no eviction is lost.
    /// Implementations with no per-page mapping (the in-memory test arena) treat this as a
    /// no-op. <paramref name="pageIdx"/> is the arena-absolute page index
    /// (<c>offset / Environment.SystemPageSize</c>).
    /// </summary>
    void QueueEviction(int arenaId, uint pageIdx);

    /// <summary>
    /// Per-arena page residency tracker. Reservations call
    /// <see cref="PageResidencyTracker.TryTouch"/> directly to record per-page accesses; the
    /// manager owns the tracker and disposes it. Instances configured with zero cache bytes
    /// (<c>PersistedSnapshotArenaPageCacheBytes = 0</c>, as in tests) return a 0-capacity tracker
    /// whose <c>TryTouch</c> is a no-op.
    /// </summary>
    PageResidencyTracker PageTracker { get; }
}
