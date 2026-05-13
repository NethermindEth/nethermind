// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public unsafe interface IArenaManager : IDisposable
{
    void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries);

    ArenaWriter CreateWriter(long estimatedSize, string tag);
    (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, long actualSize, string tag);

    void CancelWrite(int arenaId, long startOffset);
    ArenaReservation Open(in SnapshotLocation location, string tag);

    /// <summary>
    /// Open a read-only view of bytes that have been written to <paramref name="arenaId"/>
    /// at the absolute range <c>[absoluteOffset, absoluteOffset + size)</c> through a still-open
    /// <see cref="ArenaWriter"/> (i.e. before <see cref="CompleteWrite"/> is called). The caller
    /// is responsible for flushing the writer's buffer first; for file-backed managers the
    /// returned view is a fresh mmap, for the in-memory test manager it borrows the pending
    /// stream's backing buffer. Used by <see cref="ArenaBufferWriter.OpenReader"/> to let an
    /// HSST index builder read back the data section it just emitted.
    /// </summary>
    IArenaWholeView OpenPendingView(int arenaId, long absoluteOffset, long size);

    void MarkDead(in SnapshotLocation location);
    void AdviseDontNeed(ArenaReservation reservation);

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
