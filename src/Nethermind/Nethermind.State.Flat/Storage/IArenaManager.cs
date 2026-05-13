// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public unsafe interface IArenaManager : IDisposable
{
    void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries);

    ArenaWriter CreateWriter(long estimatedSize, string tag);
    ArenaReservation Open(in SnapshotLocation location, string tag);

    /// <summary>
    /// Drop <paramref name="deadSize"/> bytes of <paramref name="file"/> as dead. The caller
    /// (typically <see cref="ArenaReservation.CleanUp"/>) handles file-side <c>madvise</c> /
    /// optional <c>posix_fadvise</c> and tracker-forget itself, so this method only does the
    /// atomic set/dict/metric bookkeeping that needs the manager's lock.
    /// </summary>
    void MarkDead(ArenaFile file, long deadSize);

    /// <summary>
    /// Drop tracker entries for every fully-covered OS page in
    /// <c>[byteOffset, byteOffset + byteSize)</c> of <paramref name="arenaId"/>. The page-
    /// rounding mirrors <see cref="ArenaFile.AdviseDontNeed"/> (offset rounded up, end rounded
    /// down) so the tracker drops the same pages the kernel was just told to forget. No-op for
    /// implementations that disable the tracker.
    /// </summary>
    void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize);

    /// <summary>
    /// Whether <see cref="ArenaReservation.CleanUp"/> should also issue a
    /// <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> after the <c>madvise(MADV_DONTNEED)</c>.
    /// </summary>
    bool FadviseOnEviction { get; }

    /// <summary>
    /// Enqueue a page eviction for asynchronous dispatch. The implementation pushes
    /// <c>(arenaId, pageIdx)</c> onto a bounded MPSC ring drained by a background worker that
    /// performs the <c>madvise(MADV_DONTNEED)</c> (and optional <c>posix_fadvise</c>) syscall
    /// off the producer thread. The drain re-checks <see cref="PageResidencyTracker.ContainsPage"/>
    /// and skips the syscall if the page returned to the working set in the meantime. On
    /// ring-full the producer falls back to inline dispatch so no eviction is lost.
    /// Implementations with no per-page mapping (the in-memory test arena) treat this as a
    /// no-op. <paramref name="pageIdx"/> is the arena-absolute page index
    /// (<c>offset / Environment.SystemPageSize</c>).
    /// </summary>
    void QueueEviction(int arenaId, int pageIdx);

    /// <summary>
    /// Per-arena page residency tracker. Reservations call
    /// <see cref="PageResidencyTracker.TryTouch"/> directly to record per-page accesses; the
    /// manager owns the tracker and disposes it. Implementations with nothing to track (e.g.
    /// the in-memory test arena) return a 0-capacity tracker whose <c>TryTouch</c> is a no-op.
    /// </summary>
    PageResidencyTracker PageTracker { get; }
}
