// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// File pool for trie-node RLP bytes, stored back-to-back in its own files, separate from
/// the metadata table arena files held by <see cref="IArenaManager"/>. A <see cref="NodeRef"/>
/// embedded in a persisted snapshot's metadata points at <c>(BlobArenaId, file-absolute
/// offset)</c>; the manager resolves the id to the underlying arena file. Standalone — owns
/// its own file pool, with no dependency on <see cref="ArenaManager"/>. Each known
/// blob file is a refcounted <see cref="BlobArenaFile"/>; the manager's array slot is
/// the file's initial lease (count=1), the writer holds an additional one for the
/// duration of <see cref="BlobArenaWriter"/>, and each leased
/// <see cref="PersistedSnapshots.PersistedSnapshot"/> takes another. The on-disk file is
/// deleted by the file's own <see cref="BlobArenaFile.CleanUp"/> when its refcount hits
/// zero (typically at manager shutdown or in <see cref="SweepUnreferenced"/>); the
/// per-file <see cref="BlobArenaFile.PersistOnShutdown"/> flag overrides delete for files
/// still referenced by loaded snapshots.
///
/// <para>
/// Wiring convention: <c>FlatWorldStateModule</c> instantiates exactly one
/// <c>(ArenaManager metadata, BlobArenaManager blobs)</c> pair, shared by the
/// persisted-snapshot repository and the compactor.
/// </para>
///
/// <para>
/// <b>One id per file.</b> A <c>BlobArenaId</c> is the file's stable numeric id
/// (narrowed to <see cref="ushort"/>) — many writers across many base snapshots append
/// into the same file over its lifetime; a writer reserves the file by removing it from
/// <c>_mutableFiles</c> and releases it (re-adding) on Complete or Cancel. A new id is
/// only minted when no existing file has headroom; with a typical 1 GiB max file size,
/// the count stays well below 65535.
/// </para>
///
/// <para>
/// <b>Storage:</b> a flat <see cref="BlobArenaFile"/>?[ushort.MaxValue + 1] array indexed
/// by id. O(1) lookup, no hash, no concurrent-dictionary overhead. Memory footprint:
/// 65 536 × 8 B ≈ 512 KiB per manager.
/// </para>
/// </summary>
public sealed class BlobArenaManager : IBlobArenaManager
{
    private const string BlobFilePrefix = "blob_";
    private const string BlobFileExtension = ".bin";

    private readonly string _basePath;
    private readonly long _maxFileSize;
    private readonly Lock _lock = new();
    // Indexed by blob arena id. Null slot = no file. Reads (TryLeaseFile lookup) are
    // unlocked — reference-slot reads are atomic in the CLR memory model. Slot mutations
    // (insert / null) happen under _lock alongside _mutableFiles.
    private readonly BlobArenaFile?[] _files = new BlobArenaFile?[ushort.MaxValue + 1];
    // Files that still have headroom for further packing AND are not currently held by
    // a writer. A writer reserves a file by removing it from this set; Complete / Cancel
    // re-add it (if room remains). Protected by _lock.
    private readonly HashSet<ushort> _mutableFiles = [];
    private int _nextFileId;
    private bool _disposed;

    public BlobArenaManager(string basePath, long maxFileSize)
    {
        _basePath = basePath;
        _maxFileSize = maxFileSize;
        Directory.CreateDirectory(basePath);
    }

    /// <summary>
    /// Rehydrate the file pool from on-disk file lengths. Must be called before any
    /// <see cref="PersistedSnapshots.PersistedSnapshot"/> is constructed so
    /// <see cref="TryLeaseFile"/> can resolve ids stored in their <c>ref_ids</c> metadata.
    /// </summary>
    public void Initialize()
    {
        using Lock.Scope scope = _lock.EnterScope();
        foreach (string path in Directory.GetFiles(_basePath, $"*{BlobFileExtension}"))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(BlobFilePrefix, StringComparison.Ordinal)) continue;
            int id = ParseId(name);
            if (id < 0 || id > ushort.MaxValue) continue;
            long len = new FileInfo(path).Length;
            long maxSize = len > 0 ? Math.Max(len, _maxFileSize) : _maxFileSize;
            BlobArenaFile file = new((ushort)id, path, maxSize, frontier: len);
            _files[id] = file;
            _nextFileId = Math.Max(_nextFileId, id + 1);
            if (len < _maxFileSize) _mutableFiles.Add((ushort)id);
        }
    }

    /// <summary>
    /// Open a writer that appends into an existing arena file with headroom (or a fresh
    /// one if none qualifies). The writer holds a lease on the underlying
    /// <see cref="BlobArenaFile"/> for its lifetime; <see cref="BlobArenaWriter.Dispose"/>
    /// drops it. The caller takes a separate snapshot lease via <see cref="TryLeaseFile"/>
    /// before disposing the writer.
    /// </summary>
    public BlobArenaWriter CreateWriter(long estimatedSize)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed)
            throw new ObjectDisposedException(nameof(BlobArenaManager));

        ushort? chosen = null;
        using ArrayPoolList<ushort> toRemove = new(0);
        foreach (ushort id in _mutableFiles)
        {
            BlobArenaFile candidate = _files[id]!;
            if (candidate.Frontier + estimatedSize <= candidate.MaxSize)
            {
                chosen = id;
                break;
            }
            toRemove.Add(id);
        }
        foreach (ushort id in toRemove) _mutableFiles.Remove(id);

        ushort fileId;
        BlobArenaFile file;
        long startOffset;
        if (chosen is ushort existing)
        {
            fileId = existing;
            file = _files[fileId]!;
            startOffset = file.Frontier;
            // Reserve: remove from the mutable set so no concurrent CreateWriter picks it.
            // OnWriteCompleted / OnWriteCancelled re-add it if it still has headroom.
            _mutableFiles.Remove(fileId);
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
            // Fresh file isn't added to _mutableFiles yet — Complete/Cancel adds it.
            startOffset = 0;
        }

        // The writer's lease keeps the file alive for the write. Mid-cleanup shouldn't happen
        // under _lock, but guard against it.
        if (!file.TryAcquireLease())
            throw new InvalidOperationException(
                $"Blob arena {fileId} is mid-cleanup; cannot open writer.");

        Stream stream = file.OpenWriteStream(startOffset);
        return new BlobArenaWriter(this, file, startOffset, stream);
    }

    /// <summary>
    /// Acquire a lease on the file identified by <paramref name="blobArenaId"/>. Returns
    /// false if the manager doesn't know the id, or if the file is mid-cleanup. The
    /// caller drops the lease by calling <see cref="BlobArenaFile.Dispose"/>.
    /// </summary>
    public bool TryLeaseFile(ushort blobArenaId, [NotNullWhen(true)] out BlobArenaFile? file)
    {
        // Lock-free: reference-slot reads are atomic and TryAcquireLease guards the race
        // where the file is mid-CleanUp (see the comment on _files). SweepUnreferenced/Dispose
        // either land before our read (slot is null) or after our lease (HasOnlyManagerLease
        // sees the extra lease and skips).
        BlobArenaFile? candidate = _files[blobArenaId];
        if (candidate is null || !candidate.TryAcquireLease())
        {
            file = null;
            return false;
        }
        file = candidate;
        return true;
    }

    /// <summary>
    /// Return the blob arena file currently registered under <paramref name="blobArenaId"/>,
    /// or throw if no slot is populated. Lock-free O(1) array read — the caller MUST already
    /// hold a lease on the file (typically acquired via <see cref="TryLeaseFile"/> at snapshot
    /// load time). Does NOT bump the refcount; used by the hot read path in
    /// <see cref="PersistedSnapshots.PersistedSnapshot"/> and by the snapshot's teardown to
    /// resolve ids it leased earlier without re-paying the lease-acquisition lock.
    /// </summary>
    public BlobArenaFile GetFile(ushort blobArenaId) =>
        _files[blobArenaId]
            ?? throw new InvalidOperationException(
                $"Blob arena {blobArenaId} not registered with this manager.");

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Complete"/> after the writer has set the file's
    /// new frontier directly. The manager learns whether the id should be a packing
    /// candidate for the next writer and pushes the post-write frontier delta to
    /// <c>Metrics.BlobAllocatedBytes</c>.
    /// </summary>
    public void OnWriteCompleted(BlobArenaFile file, bool hasHeadroom)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (hasHeadroom) _mutableFiles.Add(file.BlobArenaId);
        // Ratchet BlobAllocatedBytes up to file.Frontier: push the delta since the last report
        // and bring ReportedFrontier in sync. Bytes are **allocated** (Frontier), not mapped
        // (MaxSize) — sparse-file zeros after the frontier are excluded.
        long delta = file.Frontier - file.ReportedFrontier;
        if (delta != 0)
        {
            file.ReportedFrontier = file.Frontier;
            Interlocked.Add(ref Metrics._blobAllocatedBytes, delta);
        }
    }

    /// <summary>
    /// Called by <see cref="BlobArenaWriter.Dispose"/> on the cancel path. The writer's
    /// frontier didn't advance, so the file still has room by construction — re-add the
    /// id to the mutable pool. No file touch.
    /// </summary>
    public void OnWriteCancelled(ushort blobArenaId)
    {
        using Lock.Scope scope = _lock.EnterScope();
        _mutableFiles.Add(blobArenaId);
    }

    /// <summary>
    /// Delete arena files that no snapshot referenced after a restart — recoverable
    /// orphans from a mid-write crash where Complete never ran (or where the owning
    /// snapshot was wiped before restart). Safe to call after every
    /// <see cref="PersistedSnapshots.PersistedSnapshotRepository.LoadFromCatalog"/>;
    /// no concurrent activity is expected at that point.
    /// </summary>
    public void SweepUnreferenced()
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        for (int id = 0; id < _files.Length; id++)
        {
            BlobArenaFile? file = _files[id];
            if (file is null) continue;
            // File still has external lease(s) — a snapshot loaded it during LoadFromCatalog.
            if (!file.HasOnlyManagerLease) continue;
            _files[id] = null;
            _mutableFiles.Remove((ushort)id);
            // Drop the manager's array-slot lease. With no other lease holders the
            // file's refcount hits zero, CleanUp runs and deletes the on-disk file
            // (preserve flag isn't set — nothing called PersistOnShutdown on this).
            file.Dispose();
        }
    }

    /// <summary>
    /// Called by <see cref="PersistedSnapshots.PersistedSnapshot.CleanUp"/> after it has
    /// released its lease on a blob file. If only the manager's slot lease remains and
    /// the file's frontier is non-zero, reset the frontier to 0 so the bytes gauge drops
    /// and the file is reusable for packing from offset 0. No-op when the file still
    /// has external lessees.
    /// </summary>
    public void TryResetOrphanedFrontier(BlobArenaFile file)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        // Slot may already have been replaced (Dispose nulls it out).
        if (_files[file.BlobArenaId] != file) return;
        // Re-check inside the lock — a racing TryLeaseFile or CreateWriter could
        // have bumped the refcount in the window between the caller's
        // HasOnlyManagerLease probe and us taking the lock.
        if (!file.HasOnlyManagerLease) return;
        // PersistedSnapshotRepository.Dispose flags every loaded blob with
        // PersistOnShutdown before disposing snapshots. The last snapshot's CleanUp
        // arrives here with HasOnlyManagerLease=true — without this guard we'd punch
        // a hole over the WHOLE [0, prev) range of a file the next session needs to
        // rehydrate intact (BlobArenaFile.CleanUp would keep the file on disk, but
        // its bytes would all read as zeros).
        if (file.IsShutdownPreserved) return;
        long prev = file.ReportedFrontier;
        if (prev == 0)
        {
            _mutableFiles.Add(file.BlobArenaId);
            return;
        }

        // Take the file out of the packing pool before mutating Frontier, preserving the
        // "files in _mutableFiles have a stable Frontier" invariant. Re-added at frontier=0 below.
        _mutableFiles.Remove(file.BlobArenaId);

        // Reclaim [0, prev) while still under _lock — a racing CreateWriter would otherwise
        // lease this file and append at offset 0, and a truncate over fresh data would corrupt
        // it. ftruncate zeros the logical length AND frees all disk blocks in one syscall; the
        // page cache for the range is implicitly invalidated.
        file.SetFileLength(0);

        file.Frontier = 0;
        file.ReportedFrontier = 0;
        Interlocked.Add(ref Metrics._blobAllocatedBytes, -prev);

        _mutableFiles.Add(file.BlobArenaId);
    }

    public void Dispose()
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        _disposed = true;
        for (int id = 0; id < _files.Length; id++)
        {
            BlobArenaFile? file = _files[id];
            if (file is null) continue;
            _files[id] = null;
            // Drop the manager's array-slot lease. If a snapshot still holds a lease,
            // the file's refcount stays positive; the snapshot's later Dispose triggers
            // CleanUp, which honours the PersistOnShutdown flag set by
            // PersistedSnapshotRepository.Dispose's first pass.
            file.Dispose();
        }
    }

    private static int ParseId(string fileName)
    {
        string noExt = Path.GetFileNameWithoutExtension(fileName);
        if (!noExt.StartsWith(BlobFilePrefix, StringComparison.Ordinal)) return -1;
        return int.TryParse(noExt.AsSpan(BlobFilePrefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out int id) ? id : -1;
    }
}
