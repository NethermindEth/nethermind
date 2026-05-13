// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// File pool for trie-node RLP bytes. Standalone — owns its own file pool, with no
/// dependency on <see cref="ArenaManager"/> or <see cref="IArenaManager"/>. Each known
/// blob file is a refcounted <see cref="BlobArenaFile"/>; the manager's dictionary entry
/// is the file's initial lease, snapshot leases are additional ones. The on-disk file is
/// deleted by the file's own <see cref="BlobArenaFile.CleanUp"/> as soon as the last
/// lease is released (unless the manager is in shutdown, in which case files are
/// preserved for the next session).
///
/// <para>
/// <b>One id per file.</b> A <c>BlobArenaId</c> is the file's stable numeric id
/// (narrowed to <see cref="ushort"/>) — many writers across many base snapshots append
/// into the same file over its lifetime, claiming the file for write via the
/// <c>_reservedFiles</c> mutual-exclusion set and releasing on Complete. A new id is
/// only minted when no existing file has headroom; with a typical 1 GiB max file size,
/// the count stays well below 65535.
/// </para>
///
/// <para>
/// <b>External-lease tracking.</b> <c>_refCounts</c> mirrors snapshot leases + at most one
/// transient writer-creation lease per in-flight <see cref="BlobArenaWriter.Complete"/>.
/// When the count reaches zero outside shutdown the manager drops its own dict ref —
/// the file's refcount then hits zero and the file self-cleans (close handle, delete on-disk).
/// </para>
/// </summary>
public sealed class BlobArenaManager : IBlobArenaManager
{
    private const string BlobFilePrefix = "blob_";
    private const string BlobFileExtension = ".bin";

    private readonly string _basePath;
    private readonly long _maxFileSize;
    private readonly string _reservationTag;
    private readonly Lock _lock = new();
    // All known files, keyed by id. ConcurrentDictionary so RandomRead-equivalent paths
    // can resolve a file without taking _lock.
    private readonly ConcurrentDictionary<ushort, BlobArenaFile> _files = new();
    // Snapshot lease + transient writer-creation lease counts per file. Protected by _lock.
    private readonly Dictionary<ushort, int> _refCounts = [];
    // Frontier captured the first time a file is exposed as a leasable handle — used to
    // keep the per-tag bytes metric stable across subsequent appends.
    private readonly Dictionary<ushort, long> _initialFrontiers = [];
    // Files currently held by a writer. Protected by _lock.
    private readonly HashSet<ushort> _reservedFiles = [];
    // Files that still have headroom for further packing. Protected by _lock.
    private readonly HashSet<ushort> _mutableFiles = [];
    private int _nextFileId;
    private bool _disposed;

    /// <summary>
    /// Construct a blob arena manager rooted at <paramref name="basePath"/> with a per-file
    /// size cap of <paramref name="maxFileSize"/>. <paramref name="reservationTag"/> tags
    /// metric updates (typically <see cref="ArenaReservationTags.BlobSmall"/> or
    /// <see cref="ArenaReservationTags.BlobLarge"/>).
    /// </summary>
    public BlobArenaManager(string basePath, long maxFileSize, string reservationTag)
    {
        _basePath = basePath;
        _maxFileSize = maxFileSize;
        _reservationTag = reservationTag;
        Directory.CreateDirectory(basePath);
    }

    /// <summary>
    /// Rehydrate the file pool from on-disk file lengths. Must be called before any
    /// <see cref="PersistedSnapshots.PersistedSnapshot"/> is constructed so
    /// <see cref="TryLeaseFile"/> can resolve ids stored in their <c>ref_ids</c> metadata.
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            foreach (string path in Directory.GetFiles(_basePath, $"*{BlobFileExtension}"))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(BlobFilePrefix, StringComparison.Ordinal)) continue;
                int id = ParseId(name);
                if (id < 0 || id > ushort.MaxValue) continue;
                long len = new FileInfo(path).Length;
                long maxSize = len > 0 ? Math.Max(len, _maxFileSize) : _maxFileSize;
                BlobArenaFile file = new((ushort)id, path, maxSize, frontier: len);
                _files[(ushort)id] = file;
                _nextFileId = Math.Max(_nextFileId, id + 1);
                if (len < _maxFileSize) _mutableFiles.Add((ushort)id);
            }
        }
    }

    /// <summary>
    /// Open a writer that appends into an existing arena file with headroom (or a fresh
    /// one if none qualifies). The writer's <see cref="BlobArenaWriter.BlobArenaId"/> is
    /// the underlying file id.
    /// </summary>
    public BlobArenaWriter CreateWriter(long estimatedSize, string tag)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BlobArenaManager));

            ushort? chosen = null;
            List<ushort>? toRemove = null;
            foreach (ushort id in _mutableFiles)
            {
                if (_reservedFiles.Contains(id)) continue;
                BlobArenaFile candidate = _files[id];
                if (candidate.Frontier + estimatedSize <= candidate.MaxSize)
                {
                    chosen = id;
                    break;
                }
                (toRemove ??= []).Add(id);
            }
            if (toRemove is not null)
                foreach (ushort id in toRemove) _mutableFiles.Remove(id);

            ushort fileId;
            BlobArenaFile file;
            long startOffset;
            if (chosen is ushort existing)
            {
                fileId = existing;
                file = _files[fileId];
                startOffset = file.Frontier;
            }
            else
            {
                if (_nextFileId > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"Blob arena file id space exhausted ({ushort.MaxValue + 1} files).");
                fileId = (ushort)_nextFileId++;
                string path = Path.Combine(_basePath, $"{BlobFilePrefix}{fileId:D4}{BlobFileExtension}");
                file = new BlobArenaFile(fileId, path, _maxFileSize, frontier: 0);
                _files[fileId] = file;
                _mutableFiles.Add(fileId);
                startOffset = 0;
            }

            _reservedFiles.Add(fileId);
            FileStream stream = file.OpenWriteStream(startOffset);
            return new BlobArenaWriter(this, fileId, startOffset, stream);
        }
    }

    public int RandomRead(ushort blobArenaId, long offset, Span<byte> destination)
    {
        if (!_files.TryGetValue(blobArenaId, out BlobArenaFile? file)) return 0;
        return file.RandomRead(offset, destination);
    }

    public bool TryLeaseFile(ushort blobArenaId, [NotNullWhen(true)] out BlobArenaFile? file)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(blobArenaId, out BlobArenaFile? candidate))
            {
                file = null;
                return false;
            }
            // TryAcquireLease guards against the race where another path is mid-CleanUp on
            // this id. On failure surface as "not found".
            if (!candidate.TryAcquireLease())
            {
                file = null;
                return false;
            }
            if (_refCounts.TryGetValue(blobArenaId, out int existing))
            {
                _refCounts[blobArenaId] = existing + 1;
            }
            else
            {
                _refCounts[blobArenaId] = 1;
                RegisterMetric(blobArenaId, candidate.Frontier);
            }
            file = candidate;
            return true;
        }
    }

    public void ReleaseBlobArena(ushort blobArenaId)
    {
        BlobArenaFile? toDropManagerRef = null;
        long initialFrontier = 0;
        bool emitMetric = false;
        lock (_lock)
        {
            if (!_refCounts.TryGetValue(blobArenaId, out int existing)) return;
            int newCount = existing - 1;
            if (newCount > 0)
            {
                _refCounts[blobArenaId] = newCount;
                return;
            }
            _refCounts.Remove(blobArenaId);
            if (_initialFrontiers.Remove(blobArenaId, out initialFrontier))
                emitMetric = true;
            // During shutdown, preserve on-disk file for the next session — Dispose drops the
            // dict ref then but CleanUp's IsDisposed check skips the File.Delete.
            if (_disposed) return;
            if (_files.TryRemove(blobArenaId, out BlobArenaFile? file))
            {
                _mutableFiles.Remove(blobArenaId);
                toDropManagerRef = file;
            }
        }
        if (emitMetric) UnregisterMetric(initialFrontier);
        // Outside the lock: drop the manager's dict ref. File self-cleans iff no other
        // lease holds it.
        toDropManagerRef?.Dispose();
    }

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Complete"/> to register the new frontier for
    /// the file. Bumps the refcount by 1 for the writer's transient creation lease — the
    /// caller (PersistedSnapshotRepository) transfers that lease to the new snapshot via
    /// <see cref="TryLeaseFile"/> then drops it via <see cref="ReleaseBlobArena"/>.
    /// </summary>
    internal void RegisterCompleted(ushort blobArenaId, long startOffset, long bytesWritten)
    {
        long newFrontier = startOffset + bytesWritten;
        lock (_lock)
        {
            BlobArenaFile file = _files[blobArenaId];
            file.Frontier = newFrontier;
            _reservedFiles.Remove(blobArenaId);
            if (newFrontier >= file.MaxSize) _mutableFiles.Remove(blobArenaId);
            if (_refCounts.TryGetValue(blobArenaId, out int existing))
            {
                _refCounts[blobArenaId] = existing + 1;
            }
            else
            {
                // The writer's transient lease is the first external ref on this file. The
                // file is at its initial count of 1 (the manager dict's lease); we need to
                // bump it via TryAcquireLease so a later ReleaseBlobArena can balance it.
                if (!file.TryAcquireLease())
                    throw new InvalidOperationException(
                        $"Blob arena {blobArenaId} was disposed mid-write; cannot register completion.");
                _refCounts[blobArenaId] = 1;
                RegisterMetric(blobArenaId, newFrontier);
            }
        }
    }

    internal void CancelWrite(ushort blobArenaId)
    {
        lock (_lock) _reservedFiles.Remove(blobArenaId);
    }

    /// <summary>
    /// Delete arena files that no snapshot referenced after a restart — recoverable
    /// orphans from a mid-write crash where Complete never ran (or where the owning
    /// snapshot was wiped before restart). Safe to call after every
    /// <see cref="PersistedSnapshots.PersistedSnapshotRepository.LoadFromCatalog"/>.
    /// </summary>
    public void SweepUnreferenced()
    {
        List<ushort>? toDelete = null;
        lock (_lock)
        {
            foreach (KeyValuePair<ushort, BlobArenaFile> kv in _files)
            {
                if (!_refCounts.ContainsKey(kv.Key))
                    (toDelete ??= []).Add(kv.Key);
            }
        }
        if (toDelete is null) return;
        foreach (ushort id in toDelete)
        {
            BlobArenaFile? toDropManagerRef = null;
            lock (_lock)
            {
                if (_disposed) return;
                if (_files.TryRemove(id, out BlobArenaFile? file))
                {
                    _mutableFiles.Remove(id);
                    toDropManagerRef = file;
                }
            }
            // Drop the manager's dict ref outside the lock. The file self-cleans.
            toDropManagerRef?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            // Drop each file's manager-dict ref. CleanUp sees IsDisposed=true so the on-disk
            // file is preserved; only the SafeFileHandle is closed.
            foreach (KeyValuePair<ushort, BlobArenaFile> kv in _files) kv.Value.Dispose();
            _files.Clear();
        }
    }

    private void RegisterMetric(ushort blobArenaId, long frontier)
    {
        _initialFrontiers[blobArenaId] = frontier;
        Metrics.ArenaReservationCountByTag.AddOrUpdate(_reservationTag, 1L, static (_, c) => c + 1);
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(_reservationTag, static (_, s) => s, static (_, b, s) => b + s, frontier);
    }

    private void UnregisterMetric(long frontier)
    {
        Metrics.ArenaReservationCountByTag.AddOrUpdate(_reservationTag, 0L, static (_, c) => Math.Max(0, c - 1));
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(_reservationTag, static (_, _) => 0L, static (_, b, s) => Math.Max(0, b - s), frontier);
    }

    private static int ParseId(string fileName)
    {
        string noExt = Path.GetFileNameWithoutExtension(fileName);
        if (!noExt.StartsWith(BlobFilePrefix, StringComparison.Ordinal)) return -1;
        return int.TryParse(noExt.AsSpan(BlobFilePrefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out int id) ? id : -1;
    }
}
