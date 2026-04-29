// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public interface IArenaManager : IDisposable
{
    void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries);
    ArenaWriter CreateWriter(int estimatedSize);
    (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, int actualSize);
    void CancelWrite(int arenaId, long startOffset);
    ArenaReservation Open(in SnapshotLocation location);
    ReadOnlySpan<byte> GetSpan(ArenaReservation reservation);
    IArenaWholeView OpenWholeView(ArenaReservation reservation);
    void MarkDead(in SnapshotLocation location);
    void AdviseDontNeed(ArenaReservation reservation);
    void Touch(ArenaReservation reservation, int subOffset, int size);

    /// <summary>
    /// MADV_DONTNEED a single OS page within <paramref name="arenaId"/>. Used by
    /// <see cref="PageClockCache"/>'s eviction callback. <paramref name="pageIdx"/> is the
    /// arena-absolute page index (<c>offset / Environment.SystemPageSize</c>).
    /// </summary>
    void AdviseDontNeedPage(int arenaId, int pageIdx);

    /// <summary>
    /// Page-level clock cache used by readers to track recent OS-page touches and trigger
    /// per-page <c>MADV_DONTNEED</c> on eviction. Null when the implementation has nothing
    /// to advise (e.g. the in-memory test arena).
    /// </summary>
    PageClockCache? PageCache { get; }
}
