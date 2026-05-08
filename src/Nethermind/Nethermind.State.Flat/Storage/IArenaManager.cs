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
    ReadOnlySpan<byte> GetSpan(ArenaReservation reservation);
    IArenaWholeView OpenWholeView(ArenaReservation reservation);

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

    /// <summary>
    /// Raw pointer to the first byte of <paramref name="reservation"/> within the
    /// owning arena's mmap. Long-offset arithmetic on the returned pointer is valid
    /// for <paramref name="size"/> bytes. Pointer lifetime matches the reservation
    /// (or, for the test arena, the manager's lifetime).
    /// </summary>
    void GetReservationPointer(ArenaReservation reservation, out byte* dataPtr, out long size);

    /// <summary>
    /// Read bytes from the reservation at <paramref name="subOffset"/> via a non-mmap
    /// file primitive (<c>pread</c>). Used by the cross-snapshot <c>NodeRef</c> deref
    /// path to avoid faulting referenced Full-snapshot pages into our resident set
    /// or polluting the per-arena <see cref="PageResidencyTracker"/>. Returns the
    /// number of bytes copied into <paramref name="destination"/> (may be less than
    /// the destination length on short read at end-of-data).
    /// </summary>
    int RandomRead(ArenaReservation reservation, long subOffset, Span<byte> destination);

    void MarkDead(in SnapshotLocation location);
    void AdviseDontNeed(ArenaReservation reservation);
    void Touch(ArenaReservation reservation, long subOffset, long size);

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

    /// <summary>
    /// Number of arena files currently held by this manager.
    /// </summary>
    int ArenaFileCount { get; }

    /// <summary>
    /// Sum of mmap sizes across all arena files in this manager (bytes).
    /// </summary>
    long ArenaMappedBytes { get; }
}
