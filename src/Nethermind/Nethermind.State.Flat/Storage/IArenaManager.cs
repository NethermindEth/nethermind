// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public interface IArenaManager : IDisposable, IPageEvictionHandler
{
    void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries);
    ArenaWriter CreateWriter(int estimatedSize, string tag);
    (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, int actualSize, string tag);
    void CancelWrite(int arenaId, long startOffset);
    ArenaReservation Open(in SnapshotLocation location, string tag);
    ReadOnlySpan<byte> GetSpan(ArenaReservation reservation);
    IArenaWholeView OpenWholeView(ArenaReservation reservation);
    void MarkDead(in SnapshotLocation location);
    void AdviseDontNeed(ArenaReservation reservation);
    void Touch(ArenaReservation reservation, int subOffset, int size);

    /// <summary>
    /// MADV_DONTNEED a single OS page within <paramref name="arenaId"/>. Used by
    /// <see cref="PageResidencyTracker"/>'s eviction callback. <paramref name="pageIdx"/> is the
    /// arena-absolute page index (<c>offset / Environment.SystemPageSize</c>).
    /// </summary>
    void AdviseDontNeedPage(int arenaId, int pageIdx);

    /// <summary>
    /// Direct-mapped page residency tracker used by readers to record recent OS-page touches
    /// and trigger per-page <c>MADV_DONTNEED</c> on eviction. Null when the implementation has
    /// nothing to advise (e.g. the in-memory test arena).
    /// </summary>
    PageResidencyTracker? PageTracker { get; }

    /// <summary>
    /// Number of arena files currently held by this manager.
    /// </summary>
    int ArenaFileCount { get; }

    /// <summary>
    /// Sum of mmap sizes across all arena files in this manager (bytes).
    /// </summary>
    long ArenaMappedBytes { get; }
}
