// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public unsafe interface IArenaManager : IDisposable, IPageEvictionHandler
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

    void MarkDead(in SnapshotLocation location);
    void AdviseDontNeed(ArenaReservation reservation);
    void Touch(ArenaReservation reservation, long subOffset, long size);

    /// <summary>
    /// MADV_DONTNEED a single OS page within <paramref name="arenaId"/>. Used by
    /// <see cref="PageResidencyTracker"/>'s eviction callback. <paramref name="pageIdx"/> is the
    /// arena-absolute page index (<c>offset / Environment.SystemPageSize</c>).
    /// </summary>
    void AdviseDontNeedPage(int arenaId, int pageIdx);

    /// <summary>
    /// Direct-mapped page residency tracker used by readers to record recent OS-page touches
    /// and trigger per-page <c>MADV_DONTNEED</c> on eviction. Implementations that have nothing
    /// to advise (e.g. the in-memory test arena) return a 0-capacity tracker whose
    /// <see cref="PageResidencyTracker.TryTouch"/> is a no-op.
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
