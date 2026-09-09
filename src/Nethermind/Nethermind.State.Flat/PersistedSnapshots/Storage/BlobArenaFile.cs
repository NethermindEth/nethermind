// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// A blob arena file storing trie-node RLP bytes. Owns its <see cref="SafeFileHandle"/>
/// and is refcounted: the owning <see cref="BlobArenaManager"/>'s array slot holds the
/// initial lease (count 1), the issuing <see cref="BlobArenaWriter"/> and every leased
/// <see cref="PersistedSnapshots.PersistedSnapshot"/> hold additional ones. The on-disk
/// file is deleted by <see cref="CleanUp"/> when the last lease is released, unless
/// <see cref="PersistOnShutdown"/> was called first — in which case the file is preserved
/// for the next session.
///
/// <para>
/// Reads use <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> directly:
/// no mmap, no page tracker, no advise — the blob path is pure <c>pread</c>.
/// </para>
///
/// <para>
/// Owns its own contribution to <see cref="Metrics.BlobFileCount"/> /
/// <see cref="Metrics.BlobAllocatedBytes"/>: count +1 on
/// construction (plus the initial <see cref="Frontier"/> as allocated bytes for rehydrated
/// files); symmetric -1 / -<see cref="ReportedFrontier"/> on <see cref="CleanUp"/>.
/// <see cref="BlobArenaManager.OnWriteCompleted"/> pushes frontier deltas as writes
/// advance. Bytes are reported as **allocated** (Frontier-based), not the pre-extended
/// sparse <see cref="MaxSize"/>.
/// </para>
/// </summary>
public sealed class BlobArenaFile : RefCountingDisposable
{
    // Treated as bool; 0 = delete on CleanUp, 1 = keep the on-disk file. Set by
    // PersistOnShutdown via Interlocked.Exchange so it is safe to call from any path.
    private int _preserveOnDispose;

    /// <summary>Stable file id, narrowed from int to ushort. Embedded in every <see cref="NodeRef"/>.</summary>
    public ushort BlobArenaId { get; }

    /// <summary>On-disk path. Deleted by <see cref="CleanUp"/> unless <see cref="PersistOnShutdown"/> opted in.</summary>
    private string Path { get; }

    /// <summary>Pre-extended file length (sparse on Linux). Writers append within this cap.</summary>
    public long MaxSize { get; }

    private SafeFileHandle Handle { get; }

    /// <summary>Next-write offset. Mutated under the manager's lock during writer registration.</summary>
    internal long Frontier { get; set; }

    /// <summary>
    /// Last value of <see cref="Frontier"/> reported to <c>Metrics.BlobAllocatedBytes</c>.
    /// Lets <see cref="BlobArenaManager"/> push frontier deltas on
    /// <see cref="BlobArenaWriter.Complete"/> without re-counting bytes it already reported.
    /// </summary>
    internal long ReportedFrontier { get; set; }

    internal BlobArenaFile(ushort id, string path, long maxSize, long frontier)
    {
        BlobArenaId = id;
        Path = path;
        MaxSize = maxSize;
        Handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        // File length tracks actual data extent — FileStream.Write auto-extends on demand,
        // so we skip the pre-extension ftruncate. Keeping length == Frontier makes
        // BlobArenaManager.Initialize's frontier restore accurate (no sparse-tail surprise)
        // and lets restored files re-enter the packing pool when they still have headroom.
        Frontier = frontier;
        ReportedFrontier = frontier;
        Interlocked.Increment(ref Metrics._blobFileCount);
        if (frontier > 0)
            Interlocked.Add(ref Metrics._blobAllocatedBytes, frontier);
    }

    /// <summary>
    /// Mark this file as "preserve on disk when its refcount hits zero". Set by
    /// <see cref="PersistedSnapshots.PersistedSnapshot.PersistOnShutdown"/> for every blob
    /// arena that a still-loaded snapshot references, so the file survives manager
    /// teardown and is rehydrated by the next session's <see cref="BlobArenaManager.Initialize"/>.
    /// Idempotent.
    /// </summary>
    public void PersistOnShutdown() => Interlocked.Exchange(ref _preserveOnDispose, 1);

    /// <summary>
    /// True iff <see cref="PersistOnShutdown"/> has been called for this file. Read by
    /// <see cref="BlobArenaManager.TryResetOrphanedFrontier"/> so an orphan-frontier reset
    /// does not punch a hole over a file the caller has promised to preserve across
    /// the next session — the file would survive on disk, but its bytes would be zeroed.
    /// </summary>
    internal bool IsShutdownPreserved => Volatile.Read(ref _preserveOnDispose) != 0;

    /// <summary>
    /// Defensive lease acquisition; returns false when the file has already entered
    /// <see cref="CleanUp"/>. Promotes <see cref="RefCountingDisposable.TryAcquireLease"/>
    /// from protected to internal so the owning manager can lease under its lock.
    /// </summary>
    internal new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>
    /// True iff the file's refcount is exactly 1 — i.e. the only outstanding lease is
    /// the manager's array slot. Used by <see cref="BlobArenaManager.SweepUnreferenced"/>
    /// to detect post-restart orphans (Initialize-loaded files that no snapshot has
    /// leased) so the manager can drop its slot and let <see cref="CleanUp"/> delete
    /// the on-disk file.
    /// </summary>
    internal bool HasOnlyManagerLease => Volatile.Read(ref _leases.Value) == 1;

    /// <summary>
    /// Read into <paramref name="destination"/> starting at <paramref name="offset"/>.
    /// Returns the total bytes copied; may be less than <c>destination.Length</c> on a short read at EOF.
    /// </summary>
    public int RandomRead(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(Handle, destination[total..], offset + total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>
    /// Open a write stream seeked to <paramref name="startOffset"/>. Caller disposes when done.
    /// </summary>
    internal FileStream OpenWriteStream(long startOffset)
    {
        FileStream fs = new(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
        fs.Seek(startOffset, SeekOrigin.Begin);
        return fs;
    }

    /// <summary>
    /// <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> over <c>[offset, offset + size)</c>,
    /// dropping the range from the OS file cache. Used when an orphaned file's frontier
    /// is reset so the stale, soon-to-be-overwritten bytes don't linger in cache.
    /// </summary>
    internal void FadviseDontNeed(long offset, long size)
    {
        bool refAdded = false;
        Handle.DangerousAddRef(ref refAdded);
        try { PosixReclaim.FadviseDontNeed((int)Handle.DangerousGetHandle(), offset, size); }
        finally { if (refAdded) Handle.DangerousRelease(); }
    }

    /// <summary>
    /// <c>posix_fadvise(POSIX_FADV_WILLNEED)</c> over <c>[offset, offset + size)</c>, asking
    /// the kernel to begin asynchronous read-ahead. Used to bulk-prefetch a base snapshot's
    /// contiguous trie-RLP region before a linked CompactSized that references it is scanned.
    /// </summary>
    internal void FadviseWillNeed(long offset, long size)
    {
        bool refAdded = false;
        Handle.DangerousAddRef(ref refAdded);
        try { PosixReclaim.FadviseWillNeed((int)Handle.DangerousGetHandle(), offset, size); }
        finally { if (refAdded) Handle.DangerousRelease(); }
    }

    /// <summary>
    /// <c>fsync(2)</c> the underlying file — block until all previously written bytes are
    /// durable on disk. Called by the persisted-snapshot convert path before the catalog
    /// records the new entry so a crash cannot leave the catalog pointing at unsynced pages.
    /// </summary>
    internal void Fsync()
    {
        bool refAdded = false;
        Handle.DangerousAddRef(ref refAdded);
        try { PosixReclaim.Fsync((int)Handle.DangerousGetHandle()); }
        finally { if (refAdded) Handle.DangerousRelease(); }
    }

    /// <summary>
    /// <c>ftruncate</c> the underlying file to <paramref name="newSize"/>. Used by
    /// <see cref="BlobArenaManager.TryResetOrphanedFrontier"/> with <paramref name="newSize"/> = 0
    /// to reclaim an orphaned file: zeros the logical length AND frees all disk blocks in
    /// a single syscall. The page cache for the truncated range is implicitly invalidated.
    /// </summary>
    internal void SetFileLength(long newSize) =>
        RandomAccess.SetLength(Handle, newSize);

    protected override void CleanUp()
    {
        Handle.Dispose();
        if (Volatile.Read(ref _preserveOnDispose) == 0)
        {
            try { File.Delete(Path); } catch { /* best-effort */ }
        }
        Interlocked.Decrement(ref Metrics._blobFileCount);
        long reported = ReportedFrontier;
        ReportedFrontier = 0;
        if (reported > 0)
            Interlocked.Add(ref Metrics._blobAllocatedBytes, -reported);
    }
}
