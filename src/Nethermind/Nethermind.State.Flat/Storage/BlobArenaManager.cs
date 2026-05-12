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
/// One <see cref="BlobArenaCatalog"/> per manager (one per tier). Ids are
/// unique within a catalog, not across tiers. A <see cref="NodeRef"/> in a
/// snapshot's metadata is resolved through its owning repo's
/// <c>BlobArenaManager</c>; nothing tries to cross tiers.
/// </para>
///
/// <para>
/// Refcount accounting: this manager tracks its own per-id refcount
/// (<see cref="_refCounts"/>) that mirrors the <see cref="ArenaReservation"/>
/// lease count for the same id. When the refcount drops to 0, the catalog
/// entry is removed *before* the reservation's <c>CleanUp</c> runs
/// <see cref="ArenaManager.MarkDead"/> (which may delete the underlying
/// file once all reservations in it are dead). Crashing between catalog
/// removal and file deletion leaves a dangling on-disk arena file with no
/// catalog entry — recoverable. The reverse order would leave a phantom
/// catalog entry pointing at a deleted file.
/// </para>
/// </summary>
public sealed class BlobArenaManager : IBlobArenaManager
{
    private readonly IArenaManager _files;
    private readonly BlobArenaCatalog _catalog;
    private readonly string _reservationTag;
    private readonly bool _ownsFiles;
    private readonly Lock _lock = new();
    private readonly Dictionary<ushort, ArenaReservation> _reservations = [];
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
    public BlobArenaManager(string basePath, long maxFileSize, BlobArenaCatalog catalog, string reservationTag)
    {
        _files = new ArenaManager(basePath, pageCacheBytes: 0, maxArenaSize: maxFileSize);
        _catalog = catalog;
        _reservationTag = reservationTag;
        _ownsFiles = true;
    }

    /// <summary>
    /// Test convenience constructor: lets a test supply its own
    /// <see cref="IArenaManager"/> (typically <see cref="MemoryArenaManager"/>)
    /// so blob arenas don't touch disk. The caller owns disposal of the
    /// supplied manager.
    /// </summary>
    public BlobArenaManager(IArenaManager files, BlobArenaCatalog catalog, string reservationTag)
    {
        _files = files;
        _catalog = catalog;
        _reservationTag = reservationTag;
        _ownsFiles = false;
    }

    public int BlobArenaFileCount => _files.ArenaFileCount;
    public long BlobArenaMappedBytes => _files.ArenaMappedBytes;

    /// <summary>
    /// Rehydrate the in-memory reservation map from the catalog's entries.
    /// Must be called before any <c>PersistedSnapshot</c> is constructed so
    /// <see cref="TryLeaseFile"/> can resolve ids stored in their
    /// <c>ref_ids</c> metadata.
    /// </summary>
    public void Initialize(IReadOnlyList<BlobArenaCatalog.Entry> entries)
    {
        // Build the location list for the underlying ArenaManager.Initialize
        // (it only uses Location off SnapshotCatalog.CatalogEntry, so synthetic
        // From/To is fine).
        List<SnapshotCatalog.CatalogEntry> locations = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            locations.Add(new SnapshotCatalog.CatalogEntry(
                entries[i].BlobArenaId, default, default, entries[i].Location));
        }
        _files.Initialize(locations);

        lock (_lock)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                BlobArenaCatalog.Entry e = entries[i];
                ArenaReservation reservation = _files.Open(e.Location, _reservationTag);
                _reservations[e.BlobArenaId] = reservation;
                // Reservations start with lease=1 (from Open). Track that as our
                // initial refcount — snapshots' Acquire calls bump it; we never
                // need to release this initial lease because it persists for the
                // lifetime of the rehydrated reservation (until the last snapshot
                // referencing it is disposed). At that point _refCounts will
                // reach 0 and we'll Remove + Dispose.
                _refCounts[e.BlobArenaId] = 1;
            }
        }
    }

    /// <summary>
    /// Open a writer for a fresh reservation. The writer's
    /// <see cref="BlobArenaWriter.Complete"/> registers the reservation here
    /// under the assigned <see cref="BlobArenaWriter.BlobArenaId"/>.
    /// </summary>
    public BlobArenaWriter CreateWriter(long estimatedSize, string tag)
    {
        ArenaWriter inner = _files.CreateWriter(estimatedSize, tag);
        ushort blobArenaId = _catalog.NextId();
        return new BlobArenaWriter(this, blobArenaId, inner);
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
        ArenaReservation? reservation;
        lock (_lock)
        {
            if (!_reservations.TryGetValue(blobArenaId, out reservation))
            {
                file = null;
                return false;
            }
            _refCounts[blobArenaId] = _refCounts[blobArenaId] + 1;
        }
        reservation.AcquireLease();
        file = new BlobArenaFile(this, blobArenaId, reservation);
        return true;
    }

    public void ReleaseBlobArena(ushort blobArenaId)
    {
        ArenaReservation? reservation;
        bool removeFromCatalog;
        bool disposedSnapshot;
        lock (_lock)
        {
            disposedSnapshot = _disposed;
            if (!_reservations.TryGetValue(blobArenaId, out reservation)) return;
            int newCount = _refCounts[blobArenaId] - 1;
            if (newCount > 0)
            {
                _refCounts[blobArenaId] = newCount;
                removeFromCatalog = false;
            }
            else
            {
                _refCounts.Remove(blobArenaId);
                _reservations.Remove(blobArenaId);
                removeFromCatalog = true;
            }
        }
        // Catalog removal must precede the reservation's Dispose — its CleanUp
        // runs ArenaManager.MarkDead, which can delete the backing file. Skip
        // the removal entirely during shutdown: the underlying ArenaManager has
        // already been disposed (its MarkDead is a no-op), and the catalog
        // entries must survive across restarts so the next session can rehydrate
        // the reservation.
        if (removeFromCatalog && !disposedSnapshot) _catalog.Remove(blobArenaId);
        reservation.Dispose();
    }

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Complete"/> to register the
    /// finalised reservation. The reservation arrives with its intrinsic
    /// 1-lease (the writer's "creation" lease); this is matched by our
    /// <see cref="_refCounts"/> starting at 1. Snapshots transfer ownership
    /// by calling <see cref="TryLeaseFile"/>; the caller then drops
    /// the writer-creation lease via <see cref="ReleaseBlobArena"/>.
    /// </summary>
    internal void RegisterCompleted(ushort blobArenaId, ArenaReservation reservation)
    {
        lock (_lock)
        {
            _reservations[blobArenaId] = reservation;
            _refCounts[blobArenaId] = 1;
        }
        _catalog.Add(new BlobArenaCatalog.Entry(
            blobArenaId,
            new SnapshotLocation(reservation.ArenaId, reservation.Offset, reservation.Size)));
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
