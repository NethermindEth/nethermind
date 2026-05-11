// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A handle held by a <see cref="PersistedSnapshots.PersistedSnapshot"/> onto
/// one referenced blob arena reservation. Bundles the reservation with a
/// callback into its owning <see cref="IBlobArenaManager"/> so disposal goes
/// through the manager's refcount + catalog-removal protocol.
///
/// <para>
/// Reads bypass the manager entirely: <see cref="RandomRead"/> calls straight
/// into <see cref="ArenaReservation.RandomRead"/>, which uses the
/// <c>ConcurrentDictionary&lt;int, ArenaFile&gt;</c> inside <see cref="ArenaManager"/>
/// for the file lookup (no lock). The manager's <c>_lock</c> is only touched
/// at lease and release.
/// </para>
///
/// <para>
/// Lifecycle: created by <see cref="IBlobArenaManager.TryLeaseFile"/> with a
/// fresh lease on the underlying reservation. The caller (typically
/// <c>PersistedSnapshotRepository</c>) populates a
/// <c>Dictionary&lt;int, BlobArenaFile&gt;</c> with one entry per referenced
/// blob arena id and hands it to the persisted snapshot. The snapshot disposes
/// each entry in its <c>CleanUp</c>. <see cref="Dispose"/> is idempotent.
/// </para>
/// </summary>
public sealed class BlobArenaFile : IDisposable
{
    private readonly IBlobArenaManager _manager;
    private readonly int _blobArenaId;
    private readonly ArenaReservation _reservation;
    private int _disposed;

    internal BlobArenaFile(IBlobArenaManager manager, int blobArenaId, ArenaReservation reservation)
    {
        _manager = manager;
        _blobArenaId = blobArenaId;
        _reservation = reservation;
    }

    public int BlobArenaId => _blobArenaId;

    /// <summary>
    /// Read <paramref name="destination"/>.Length bytes starting at
    /// <paramref name="offset"/> within this blob arena reservation. Returns
    /// the number of bytes actually read (may be less than the destination
    /// length on short read at end-of-reservation).
    /// </summary>
    public int RandomRead(long offset, Span<byte> destination) =>
        _reservation.RandomRead(offset, destination);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _manager.ReleaseBlobArena(_blobArenaId);
    }
}
