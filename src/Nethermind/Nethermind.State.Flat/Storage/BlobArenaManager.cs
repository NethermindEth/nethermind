// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// File pool for trie-node RLP bytes. Standalone — owns its own
/// <see cref="ArenaManager"/> (page tracker disabled). Each pool tier
/// instantiates one <see cref="BlobArenaManager"/> alongside its metadata
/// <see cref="ArenaManager"/>; the pair <c>(ArenaManager metadata,
/// BlobArenaManager blobs)</c> together backs one tier (Small or Large).
///
/// <para>
/// <b>One id per file.</b> A <c>BlobArenaId</c> is the underlying
/// <c>ArenaFile.Id</c> (narrowed to ushort) — many writers across many base
/// snapshots append into the same file over its lifetime, claiming the file
/// for write via the inner <see cref="ArenaManager"/>'s <c>_reservedArenas</c>
/// mutual-exclusion and releasing on Complete. A new id is only minted when no
/// existing file has headroom; with a typical 1 GiB max file size, the count
/// stays well below 65535.
/// </para>
///
/// <para>
/// <b>One whole-file <see cref="ArenaReservation"/> per known file id.</b>
/// Created lazily on first <see cref="RegisterCompleted"/> or first
/// <see cref="TryLeaseFile"/> (whichever comes first), covering
/// <c>[0, frontier)</c>. Subsequent writers for the same file grow the
/// reservation's <c>Size</c> rather than allocating a new one. Snapshots
/// <see cref="TryLeaseFile"/> the reservation; the per-id <c>_refCounts</c>
/// counts snapshot leases (plus the transient writer-creation lease that
/// <see cref="PersistedSnapshots.PersistedSnapshotRepository.ConvertSnapshotToPersistedSnapshot"/>
/// drops once the new snapshot takes its own lease). When the count reaches
/// zero the reservation is disposed; <c>CleanUp</c> runs
/// <see cref="IArenaManager.MarkDead"/> over the file's full span, which
/// deletes the file.
/// </para>
///
/// <para>
/// Read offsets are file-absolute: callers pass <c>RandomRead(id, fileOffset,
/// dest)</c>. The reservation's <c>Offset</c> is 0, so the underlying
/// manager's <c>reservation.Offset + subOffset</c> degenerates to
/// <c>subOffset</c>.
/// </para>
///
/// <para>
/// Assumption: a snapshot never releases a file while another writer is
/// mid-write into the same file. In practice persistence writes then leases —
/// the producer (PersistenceManager.AddToPersistence) never prunes what it
/// just wrote — so the writer's transient lease always covers the gap.
/// </para>
/// </summary>
public sealed class BlobArenaManager : IBlobArenaManager
{
    private readonly IArenaManager _files;
    private readonly string _reservationTag;
    private readonly bool _ownsFiles;
    private readonly Lock _lock = new();
    // One reservation per known file id, covering [0, current frontier). Size grows as
    // subsequent writers append. Created lazily on first registration or first lease.
    private readonly Dictionary<ushort, ArenaReservation> _reservations = [];
    // Per-file refcount: snapshot leases + at most one transient writer-creation lease
    // per in-flight Complete. Mirrors the underlying reservation's lease count.
    private readonly Dictionary<ushort, int> _refCounts = [];
    private bool _disposed;

    /// <summary>
    /// Production constructor: BlobArenaManager owns its own file pool. The
    /// internal arena manager is disposed when this manager is disposed.
    /// <paramref name="reservationTag"/> is the <see cref="ArenaReservation.Tag"/>
    /// applied to every reservation this manager opens (e.g.
    /// <see cref="ArenaReservationTags.BlobSmall"/> or
    /// <see cref="ArenaReservationTags.BlobLarge"/>).
    /// </summary>
    public BlobArenaManager(string basePath, long maxFileSize, string reservationTag)
    {
        _files = new ArenaManager(basePath, pageCacheBytes: 0, maxArenaSize: maxFileSize);
        _reservationTag = reservationTag;
        _ownsFiles = true;
    }

    /// <summary>
    /// Test convenience constructor: lets a test supply its own
    /// <see cref="IArenaManager"/> (typically <see cref="MemoryArenaManager"/>)
    /// so blob arenas don't touch disk. The caller owns disposal of the
    /// supplied manager.
    /// </summary>
    public BlobArenaManager(IArenaManager files, string reservationTag)
    {
        _files = files;
        _reservationTag = reservationTag;
        _ownsFiles = false;
    }

    public int BlobArenaFileCount => _files.ArenaFileCount;
    public long BlobArenaMappedBytes => _files.ArenaMappedBytes;

    /// <summary>
    /// Rehydrate the underlying file pool from on-disk file lengths. Must be called
    /// before any <see cref="PersistedSnapshots.PersistedSnapshot"/> is constructed so
    /// <see cref="TryLeaseFile"/> can resolve ids stored in their <c>ref_ids</c> metadata.
    /// Whole-file reservations are created lazily on first lease.
    /// </summary>
    public void Initialize() => _files.InitializeFromFileLengths();

    /// <summary>
    /// Open a writer that appends into an existing arena file with headroom (or a
    /// fresh one if none qualifies). The writer's <see cref="BlobArenaWriter.BlobArenaId"/>
    /// is the underlying <c>ArenaFile.Id</c>.
    /// </summary>
    public BlobArenaWriter CreateWriter(long estimatedSize, string tag)
    {
        ArenaWriter inner = _files.CreateWriter(estimatedSize, tag);
        int arenaId = inner.ArenaId;
        if ((uint)arenaId > ushort.MaxValue)
            throw new InvalidOperationException(
                $"Blob arena file id {arenaId} exceeds ushort range — packing degraded?");
        return new BlobArenaWriter(this, (ushort)arenaId, inner.StartOffset, inner);
    }

    public int RandomRead(ushort blobArenaId, long offset, Span<byte> destination)
    {
        ArenaReservation? reservation;
        lock (_lock)
        {
            if (!_reservations.TryGetValue(blobArenaId, out reservation))
                return 0;
        }
        return _files.RandomRead(reservation, offset, destination);
    }

    public bool TryLeaseFile(ushort blobArenaId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BlobArenaFile? file)
    {
        ArenaReservation reservation;
        lock (_lock)
        {
            if (!_reservations.TryGetValue(blobArenaId, out ArenaReservation? existing))
            {
                if (!_files.TryGetFrontier(blobArenaId, out long frontier))
                {
                    file = null;
                    return false;
                }
                // Lazy whole-file reservation: occurs on the load path before any writer
                // for this id has run in this process.
                existing = _files.Open(new SnapshotLocation(blobArenaId, 0, frontier), _reservationTag);
                _reservations[blobArenaId] = existing;
                _refCounts[blobArenaId] = 0;
            }
            _refCounts[blobArenaId] = _refCounts[blobArenaId] + 1;
            reservation = existing;
        }
        reservation.AcquireLease();
        file = new BlobArenaFile(this, blobArenaId, reservation);
        return true;
    }

    public void ReleaseBlobArena(ushort blobArenaId)
    {
        ArenaReservation? reservation;
        bool disposedSnapshot;
        lock (_lock)
        {
            disposedSnapshot = _disposed;
            if (!_reservations.TryGetValue(blobArenaId, out reservation)) return;
            int newCount = _refCounts[blobArenaId] - 1;
            if (newCount > 0)
            {
                _refCounts[blobArenaId] = newCount;
                reservation = null;
            }
            else
            {
                _refCounts.Remove(blobArenaId);
                _reservations.Remove(blobArenaId);
            }
        }
        // Skip the dispose during shutdown so the on-disk file survives across restarts;
        // CleanUp's MarkDead would otherwise delete it.
        if (reservation is not null && !disposedSnapshot) reservation.Dispose();
    }

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Complete"/> to register the new frontier for
    /// the file. On first registration creates the whole-file reservation; otherwise grows
    /// the existing reservation's <see cref="ArenaReservation.Size"/>. Bumps
    /// <see cref="_refCounts"/> by 1 for the writer's transient creation lease — the
    /// caller (PersistedSnapshotRepository) transfers that lease to the new snapshot via
    /// <see cref="TryLeaseFile"/> then drops it via <see cref="ReleaseBlobArena"/>.
    /// </summary>
    internal void RegisterCompleted(ushort blobArenaId, long startOffset, long bytesWritten)
    {
        long newFrontier = startOffset + bytesWritten;
        ArenaReservation? newReservation = null;
        lock (_lock)
        {
            if (_reservations.TryGetValue(blobArenaId, out ArenaReservation? existing))
            {
                existing.Size = newFrontier;
                _refCounts[blobArenaId] = _refCounts[blobArenaId] + 1;
                return;
            }
            newReservation = _files.Open(
                new SnapshotLocation(blobArenaId, 0, newFrontier), _reservationTag);
            _reservations[blobArenaId] = newReservation;
            _refCounts[blobArenaId] = 1;
        }
    }

    /// <summary>
    /// Delete arena files that no snapshot referenced after a restart — recoverable
    /// orphans from a mid-write crash where Complete never ran (or where the owning
    /// snapshot was wiped before restart). Safe to call after every
    /// <see cref="PersistedSnapshots.PersistedSnapshotRepository.LoadFromCatalog"/>.
    /// </summary>
    public void SweepUnreferenced()
    {
        List<int>? toDelete = null;
        lock (_lock)
        {
            foreach (int id in _files.KnownArenaIds)
            {
                if (!_reservations.ContainsKey((ushort)id))
                    (toDelete ??= []).Add(id);
            }
        }
        if (toDelete is null) return;
        foreach (int id in toDelete) _files.DeleteFile(id);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_ownsFiles) _files.Dispose();
    }
}
