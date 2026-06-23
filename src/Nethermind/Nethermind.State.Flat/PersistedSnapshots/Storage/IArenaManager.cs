// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

public interface IArenaManager : IDisposable
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
    /// <c>posix_fadvise</c> itself, so this method only does the atomic set/dict/metric
    /// bookkeeping that needs the manager's lock.
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
}
