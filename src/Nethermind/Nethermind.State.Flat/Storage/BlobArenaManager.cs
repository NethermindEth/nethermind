// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// File pool for trie-node RLP bytes. Standalone — owns its own file pool, with no
/// dependency on <see cref="ArenaManager"/>, <see cref="IArenaManager"/>, or
/// <see cref="ArenaFile"/>. Each known blob file is represented internally as a
/// <see cref="BlobFileEntry"/> that owns a single read/write <see cref="SafeFileHandle"/>;
/// the manager hands its handle (borrowed, not transferred) to every leased
/// <see cref="BlobArenaFile"/> so reads dispatch straight into
/// <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/>.
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
/// <b>Per-id refcount.</b> <c>_refCounts</c> mirrors the snapshot leases + at most one
/// transient writer-creation lease per in-flight <see cref="BlobArenaWriter.Complete"/>.
/// When the count reaches zero outside of shutdown the file is closed and deleted; during
/// shutdown the file is preserved so the next session can rehydrate it via
/// <see cref="Initialize"/>.
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
    // can resolve a handle without taking _lock.
    private readonly ConcurrentDictionary<ushort, BlobFileEntry> _files = new();
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
                BlobFileEntry entry = new(path, maxSize, frontier: len);
                _files[(ushort)id] = entry;
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
                BlobFileEntry candidate = _files[id];
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
            BlobFileEntry entry;
            long startOffset;
            if (chosen is ushort existing)
            {
                fileId = existing;
                entry = _files[fileId];
                startOffset = entry.Frontier;
            }
            else
            {
                if (_nextFileId > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"Blob arena file id space exhausted ({ushort.MaxValue + 1} files).");
                fileId = (ushort)_nextFileId++;
                string path = Path.Combine(_basePath, $"{BlobFilePrefix}{fileId:D4}{BlobFileExtension}");
                entry = new BlobFileEntry(path, _maxFileSize, frontier: 0);
                _files[fileId] = entry;
                _mutableFiles.Add(fileId);
                startOffset = 0;
            }

            _reservedFiles.Add(fileId);
            FileStream stream = entry.OpenWriteStream(startOffset);
            return new BlobArenaWriter(this, fileId, startOffset, stream);
        }
    }

    public int RandomRead(ushort blobArenaId, long offset, Span<byte> destination)
    {
        if (!_files.TryGetValue(blobArenaId, out BlobFileEntry? entry)) return 0;
        SafeFileHandle handle = entry.Handle;
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    public bool TryLeaseFile(ushort blobArenaId, [NotNullWhen(true)] out BlobArenaFile? file)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(blobArenaId, out BlobFileEntry? entry))
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
                RegisterMetric(blobArenaId, entry.Frontier);
            }
            file = new BlobArenaFile(this, blobArenaId, entry.Handle);
            return true;
        }
    }

    public void ReleaseBlobArena(ushort blobArenaId)
    {
        BlobFileEntry? toDispose = null;
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
            // During shutdown, preserve on-disk file for the next session — close handles
            // only (done by Dispose). Do NOT delete here.
            if (_disposed) return;
            if (_files.TryRemove(blobArenaId, out BlobFileEntry? entry))
            {
                _mutableFiles.Remove(blobArenaId);
                toDispose = entry;
            }
        }
        if (emitMetric) UnregisterMetric(initialFrontier);
        if (toDispose is not null)
        {
            string path = toDispose.Path;
            toDispose.Dispose();
            try { File.Delete(path); } catch { /* best-effort */ }
        }
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
            BlobFileEntry entry = _files[blobArenaId];
            entry.Frontier = newFrontier;
            _reservedFiles.Remove(blobArenaId);
            if (newFrontier >= entry.MaxSize) _mutableFiles.Remove(blobArenaId);
            if (_refCounts.TryGetValue(blobArenaId, out int existing))
            {
                _refCounts[blobArenaId] = existing + 1;
            }
            else
            {
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
            foreach (KeyValuePair<ushort, BlobFileEntry> kv in _files)
            {
                if (!_refCounts.ContainsKey(kv.Key))
                    (toDelete ??= []).Add(kv.Key);
            }
        }
        if (toDelete is null) return;
        foreach (ushort id in toDelete)
        {
            BlobFileEntry? toDispose = null;
            lock (_lock)
            {
                if (_disposed) return;
                if (_files.TryRemove(id, out BlobFileEntry? entry))
                {
                    _mutableFiles.Remove(id);
                    toDispose = entry;
                }
            }
            if (toDispose is not null)
            {
                string path = toDispose.Path;
                toDispose.Dispose();
                try { File.Delete(path); } catch { /* best-effort */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (KeyValuePair<ushort, BlobFileEntry> kv in _files) kv.Value.Dispose();
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

    /// <summary>
    /// Per-file state owned by <see cref="BlobArenaManager"/>. Holds the single shared
    /// read/write <see cref="SafeFileHandle"/> plus the path, frontier, and max size.
    /// Multiple <see cref="BlobArenaFile"/> leases borrow <see cref="Handle"/>; the
    /// entry's <see cref="Dispose"/> closes the handle on file deletion or manager
    /// teardown.
    /// </summary>
    private sealed class BlobFileEntry : IDisposable
    {
        public string Path { get; }
        public long MaxSize { get; }
        public SafeFileHandle Handle { get; }
        public long Frontier { get; set; }

        public BlobFileEntry(string path, long maxSize, long frontier)
        {
            Path = path;
            MaxSize = maxSize;
            Handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            // Extend file to maxSize if smaller (sparse on Linux via ftruncate) so subsequent
            // appends never have to grow it.
            if (RandomAccess.GetLength(Handle) < maxSize)
                RandomAccess.SetLength(Handle, maxSize);
            Frontier = frontier;
        }

        public FileStream OpenWriteStream(long startOffset)
        {
            FileStream fs = new(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
            fs.Seek(startOffset, SeekOrigin.Begin);
            return fs;
        }

        public void Dispose() => Handle.Dispose();
    }
}
