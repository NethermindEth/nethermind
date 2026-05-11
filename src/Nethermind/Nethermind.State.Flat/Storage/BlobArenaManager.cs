// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// File pool for trie-node RLP bytes. Standalone — does not borrow an
/// <see cref="IArenaManager"/> from anyone. Each pool tier instantiates its own
/// <see cref="BlobArenaManager"/> alongside its <see cref="ArenaManager"/>; the
/// pair <c>(ArenaManager metadata, BlobArenaManager blobs)</c> together backs one
/// tier (Small or Large).
///
/// <para>
/// Internally a <see cref="BlobArenaManager"/> composes a plain
/// <see cref="ArenaManager"/> with its page residency tracker disabled
/// (<c>pageCacheBytes: 0</c>). Blob arenas do not need per-page tracking — the
/// metadata HSST's tracker already covers the bytes that fault the RLP into the
/// resident set on dereference, and tracking the blob pages separately would just
/// duplicate evictions.
/// </para>
///
/// <para>
/// A <c>BlobArenaId</c> is assigned per writer-completion; multiple ids can share
/// a backing arena file. The reservation behind an id provides the
/// <see cref="ArenaReservation"/> lease that drives file deletion once all
/// reservations in a file are dead (see <see cref="ArenaManager.MarkDead"/>).
/// </para>
///
/// <para>
/// Pass-1 scaffolding: constructed but not yet referenced by the
/// builder/repository/reader. The in-memory <see cref="_reservations"/> map is not
/// rehydrated from the catalog on restart yet — that wiring lands in pass 2 along
/// with the catalog-schema bump.
/// </para>
/// </summary>
public sealed class BlobArenaManager : IBlobArenaManager
{
    // Underlying file pool — disabled page tracker (pageCacheBytes: 0) makes the
    // PageResidencyTracker a no-op, so there are no eviction queues or drain tasks
    // associated with blob storage.
    private readonly IArenaManager _files;
    private readonly bool _ownsFiles;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, ArenaReservation> _reservations = [];
    private int _nextBlobArenaId;
    private bool _disposed;

    /// <summary>
    /// Production constructor: BlobArenaManager owns its own file pool. The internal
    /// arena manager is disposed when this manager is disposed.
    /// </summary>
    public BlobArenaManager(string basePath, long maxFileSize)
    {
        _files = new ArenaManager(basePath, pageCacheBytes: 0, maxArenaSize: maxFileSize);
        _ownsFiles = true;
    }

    /// <summary>
    /// Test convenience constructor: lets a test supply its own
    /// <see cref="IArenaManager"/> (typically <see cref="MemoryArenaManager"/>) so
    /// blob arenas don't touch disk. The caller owns disposal of the supplied
    /// manager.
    /// </summary>
    public BlobArenaManager(IArenaManager files)
    {
        _files = files;
        _ownsFiles = false;
    }

    public int BlobArenaFileCount => _files.ArenaFileCount;
    public long BlobArenaMappedBytes => _files.ArenaMappedBytes;

    /// <summary>
    /// Open a writer for a fresh reservation. The writer returns a <see cref="NodeRef"/>
    /// per stored RLP; on <see cref="BlobArenaWriter.Complete"/> the reservation is
    /// registered here under a globally-unique blob arena id.
    /// </summary>
    public BlobArenaWriter CreateWriter(long estimatedSize, string tag)
    {
        ArenaWriter inner = _files.CreateWriter(estimatedSize, tag);
        int blobArenaId;
        lock (_lock) blobArenaId = _nextBlobArenaId++;
        return new BlobArenaWriter(this, blobArenaId, inner);
    }

    public int RandomRead(int blobArenaId, long offset, Span<byte> destination)
    {
        ArenaReservation? reservation;
        lock (_lock)
        {
            if (!_reservations.TryGetValue(blobArenaId, out reservation))
                return 0;
        }
        return _files.RandomRead(reservation, offset, destination);
    }

    public bool TryAcquireBlobArena(int blobArenaId)
    {
        ArenaReservation? reservation;
        lock (_lock)
        {
            if (!_reservations.TryGetValue(blobArenaId, out reservation))
                return false;
        }
        reservation.AcquireLease();
        return true;
    }

    public void ReleaseBlobArena(int blobArenaId)
    {
        ArenaReservation? reservation;
        lock (_lock)
        {
            if (!_reservations.TryGetValue(blobArenaId, out reservation))
                return;
        }
        // Disposing the reservation once releases one lease. When the last lease drops,
        // the reservation's CleanUp runs ArenaManager.MarkDead, which deletes the
        // backing arena file once every reservation in it is dead.
        reservation.Dispose();
    }

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Complete"/> to register the finalised
    /// reservation. The reservation arrives with its intrinsic 1-lease (the writer's
    /// "creation" lease); a downstream snapshot transfers ownership by calling
    /// <see cref="AcquireBlobArena"/>, after which the writer's
    /// <see cref="BlobArenaWriter.Dispose"/> can safely release its lease.
    /// </summary>
    internal void RegisterCompleted(int blobArenaId, ArenaReservation reservation)
    {
        lock (_lock)
        {
            _reservations[blobArenaId] = reservation;
        }
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
