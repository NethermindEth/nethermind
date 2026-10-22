// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// A single append-only arena file for storing persisted snapshot table data.
/// Reads use a read-only mmap for zero-copy access; writes go through a
/// <see cref="FileStream"/> seeked to the target offset.
///
/// <para>
/// Lifecycle is refcounted: the owning <see cref="ArenaManager"/>'s dictionary entry
/// holds the initial lease (count 1). Each <see cref="ArenaReservation"/> referencing
/// the file holds an additional lease. The manager drops its lease via <see cref="Dispose"/>
/// (typically through <see cref="ArenaManager.MarkDead"/> or one of the cancel paths
/// <see cref="ArenaManager.OnWriteCancelledShared"/> / <see cref="ArenaManager.OnWriteCancelledDedicated"/>);
/// the on-disk file is deleted by <see cref="CleanUp"/> when the last lease is released,
/// unless the manager is in shutdown — in which case the file is preserved for the
/// next session.
/// </para>
/// </summary>
public sealed unsafe class ArenaFile : RefCountingDisposable
{
    private const int MADV_NORMAL = 0;
    private const int MADV_RANDOM = 1;
    private const int MADV_DONTNEED = 4;
    private const int MADV_POPULATE_READ = 22;
    private static readonly nuint PageSize = (nuint)Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(void* addr, nuint length, int advice);

    private readonly SafeFileHandle _handle;
    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private byte* _basePtr;
    // Treated as bool; 0 = delete on CleanUp, 1 = keep the on-disk file. Set by
    // PersistOnShutdown via Interlocked.Exchange so it is safe to call from any path.
    private int _preserveOnDispose;

    /// <summary>Raw pointer to the first byte of the arena's mmap. Long-offset arithmetic OK across the full <see cref="MappedSize"/>.</summary>
    public byte* BasePtr => _basePtr;

    public int Id { get; }
    private string Path { get; }
    public long MappedSize { get; private set; }

    /// <summary>
    /// True for arenas holding sub-CompactSize snapshots (the <c>PersistedBase</c> and
    /// <c>PersistedSmallCompacted</c> tiers). Those snapshots are written almost as often as the
    /// larger tiers but are demoted right after compaction and rarely read again, so they live in
    /// their own files (and their own mutable pool in <see cref="ArenaManager"/>) to keep cold,
    /// write-heavy data off the hot working set.
    /// </summary>
    public bool Small { get; }

    /// <summary>
    /// Next-write offset within this arena (in bytes). Set by <see cref="ArenaWriter.Complete"/>
    /// directly so the manager doesn't have to keep a parallel dict; read by
    /// <see cref="ArenaManager.MarkDead"/> to detect "all bytes dead" and by writer-allocation
    /// to choose the next write offset for shared (non-dedicated) arenas.
    /// </summary>
    internal long Frontier { get; set; }

    /// <summary>
    /// Cumulative bytes marked dead by <see cref="ArenaManager.MarkDead"/>. When this reaches
    /// <see cref="Frontier"/> the arena has no live data and the manager drops it. Per-file
    /// state held on the file itself so the manager doesn't keep a parallel dict.
    /// </summary>
    internal long DeadBytes { get; set; }

    /// <summary>
    /// True while an <see cref="ArenaWriter"/> holds this shared arena. Guarded by the manager's
    /// lock. Keeps <see cref="ArenaManager.MarkDead"/> from treating the arena as fully dead (and
    /// deleting the file) while a write with bytes not yet published to <see cref="Frontier"/> is
    /// in flight.
    /// </summary>
    internal bool WriterActive { get; set; }

    /// <summary>
    /// Last value of <see cref="Frontier"/> reported to <c>Metrics.ArenaAllocatedBytes</c>.
    /// Lets <see cref="ArenaManager"/> push frontier deltas on writer.Complete without
    /// keeping a parallel dict and without re-counting bytes it already reported.
    /// </summary>
    internal long ReportedFrontier { get; set; }

    // Push-style gauge updates, called by ArenaManager under its lock at every file add / remove site.
    // The bytes gauge tracks **allocated** bytes (Frontier — what's been written), not the pre-extended
    // mmap region.

    internal void ReportAdded()
    {
        Interlocked.Increment(ref Metrics._arenaFileCount);
        long frontier = Frontier;
        ReportedFrontier = frontier;
        if (frontier > 0)
            Interlocked.Add(ref Metrics._arenaAllocatedBytes, frontier);
    }

    internal void ReportRemoved()
    {
        Interlocked.Decrement(ref Metrics._arenaFileCount);
        long reported = ReportedFrontier;
        ReportedFrontier = 0;
        if (reported > 0)
            Interlocked.Add(ref Metrics._arenaAllocatedBytes, -reported);
    }

    public ArenaFile(int id, string path, long mappedSize, bool small = false)
    {
        Id = id;
        Path = path;
        MappedSize = mappedSize;
        Small = small;

        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Extend to mappedSize (sparse on Linux via ftruncate).
        if (RandomAccess.GetLength(_handle) < mappedSize)
            RandomAccess.SetLength(_handle, mappedSize);

        OpenMmap(mappedSize);
    }

    /// <summary>
    /// Try to acquire a lease without throwing on a disposing file. Returns false when the
    /// file is already in cleanup.
    /// </summary>
    internal new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>
    /// Create a write stream seeked to <paramref name="startOffset"/>.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    internal FileStream CreateWriteStream(long startOffset)
    {
        FileStream fs = new(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
        fs.Seek(startOffset, SeekOrigin.Begin);
        return fs;
    }

    /// <summary>
    /// Shrink the file to <paramref name="newSize"/> in place: close the current mmap view,
    /// <c>SetLength</c> on the underlying handle, then reopen the mmap at the new size.
    /// Refcount is untouched — the same <see cref="ArenaFile"/> instance survives across the
    /// resize so any reservations capturing it stay valid (pre-resize <see cref="BasePtr"/>
    /// values are invalidated, but the trim path only runs before any reservation is created
    /// against this file). The caller must hold the manager's lock.
    /// </summary>
    internal void Truncate(long newSize)
    {
        if (newSize == MappedSize) return;
        CloseMmap();
        RandomAccess.SetLength(_handle, newSize);
        MappedSize = newSize;
        OpenMmap(newSize);
    }

    [MemberNotNull(nameof(_mmf), nameof(_accessor))]
    private void OpenMmap(long size)
    {
        _mmf = MemoryMappedFile.CreateFromFile(_handle, mapName: null, size, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _accessor = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
        _basePtr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

        if (OperatingSystem.IsLinux())
            Madvise(_basePtr, (nuint)size, MADV_RANDOM);
    }

    private void CloseMmap()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
        _basePtr = null;
    }

    public void AdviseDontNeed(long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;

        if (TryAlignInward(offset, size, out nuint start, out nuint len))
            Madvise(_basePtr + start, len, MADV_DONTNEED);
    }

    // Round offset up to page boundary, round end down — only cover full pages.
    private static bool TryAlignInward(long offset, long size, out nuint start, out nuint len)
    {
        nuint pageSize = PageSize;
        start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        len = end - start;
        return end > start;
    }

    /// <summary>
    /// madvise(MADV_POPULATE_READ) on the page-aligned subrange of <c>[offset, offset+size)</c>.
    /// On Linux ≥ 5.14 the kernel pre-faults the pages so the next read does not block on a page
    /// fault. On older kernels the call returns <c>EINVAL</c>, which is benign and ignored.
    /// </summary>
    public void PopulateRead(long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;

        if (TryAlignInward(offset, size, out nuint start, out nuint len))
            Madvise(_basePtr + start, len, MADV_POPULATE_READ);
    }

    /// <summary>
    /// Volatile single-byte read at <paramref name="offset"/> within this arena's mmap. Used by
    /// the keep-warm path to refresh the kernel's LRU position on a resident page. Caller must
    /// hold a lease (<see cref="TryAcquireLease"/>) so <see cref="BasePtr"/> stays valid for the
    /// duration of the read — unlike <see cref="AdviseDontNeed"/>, a userspace load on a torn-down
    /// mapping would SIGSEGV instead of returning a syscall error.
    /// </summary>
    public byte TouchByte(long offset) => Volatile.Read(ref *(_basePtr + offset));

    /// <summary>
    /// posix_fadvise(POSIX_FADV_DONTNEED) on the underlying file descriptor for the
    /// page-aligned subrange of <c>[offset, offset+size)</c>. Drops the corresponding
    /// pages from the OS file cache. Redundant with <see cref="AdviseDontNeed"/> on
    /// Linux for shared mappings, but useful for benchmarking to ensure arena pages
    /// don't pollute the file cache.
    /// </summary>
    public void FadviseDontNeed(long offset, long size)
    {
        bool refAdded = false;
        _handle.DangerousAddRef(ref refAdded);
        try { PosixReclaim.FadviseDontNeed((int)_handle.DangerousGetHandle(), offset, size); }
        finally { if (refAdded) _handle.DangerousRelease(); }
    }

    /// <summary>
    /// <c>fallocate(PUNCH_HOLE | KEEP_SIZE)</c> over the page-aligned subrange of
    /// <c>[offset, offset + size)</c>, freeing the dead range's disk blocks without
    /// changing the file length. Punched pages read back as zero through the mmap.
    /// </summary>
    /// <returns>The <see cref="PosixReclaim.PunchHoleOutcome"/> reported by the kernel.</returns>
    internal PosixReclaim.PunchHoleOutcome PunchHole(long offset, long size)
    {
        bool refAdded = false;
        _handle.DangerousAddRef(ref refAdded);
        try { return PosixReclaim.TryPunchHole((int)_handle.DangerousGetHandle(), offset, size); }
        finally { if (refAdded) _handle.DangerousRelease(); }
    }

    /// <summary>
    /// <c>fsync(2)</c> the underlying file — block until all previously written bytes are
    /// durable on disk. Called by the persisted-snapshot convert/compact paths before the
    /// catalog records the new entry so a crash cannot leave the catalog pointing at
    /// unsynced pages.
    /// </summary>
    internal void Fsync()
    {
        bool refAdded = false;
        _handle.DangerousAddRef(ref refAdded);
        try { PosixReclaim.Fsync((int)_handle.DangerousGetHandle()); }
        finally { if (refAdded) _handle.DangerousRelease(); }
    }

    /// <summary>
    /// Open a fresh per-reservation mmap view over <c>[offset, offset+size)</c> with
    /// <c>MADV_NORMAL</c> hint, distinct from the global random-access view used by point
    /// queries. When <paramref name="adviseDontNeedOnDispose"/> is true, disposing the
    /// returned view applies <c>MADV_DONTNEED</c> to the range before releasing the
    /// mapping; when false the disposer just unmaps.
    /// </summary>
    internal MmapWholeView OpenWholeView(long offset, long size, bool adviseDontNeedOnDispose)
    {
        MemoryMappedViewAccessor accessor = _mmf.CreateViewAccessor(offset, size, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        // The accessor's pointer is offset by an internal page-aligned skew; add it
        // so the span starts at the requested offset's first byte.
        byte* dataPtr = ptr + accessor.PointerOffset;
        if (OperatingSystem.IsLinux())
            Madvise(dataPtr, (nuint)size, MADV_NORMAL);
        return new MmapWholeView(accessor, dataPtr, size, adviseDontNeedOnDispose);
    }

    /// <summary>
    /// A scoped read-only mmap view over a reservation's bytes: a fresh per-reservation accessor with the
    /// <c>MADV_NORMAL</c> hint, distinct from the global random-access view used by point queries. When
    /// <c>adviseDontNeedOnDispose</c> is set, disposing applies <c>MADV_DONTNEED</c> to the range so the
    /// kernel can reclaim those pages from the page cache.
    /// </summary>
    internal sealed unsafe class MmapWholeView(
        MemoryMappedViewAccessor accessor, byte* dataPtr, long size, bool adviseDontNeedOnDispose) : IDisposable
    {
        /// <summary>
        /// Raw pointer to the first byte of the view. Long-offset arithmetic is valid for the entire
        /// <see cref="Size"/> range; the mapping is kept alive until <see cref="Dispose"/>. Reservations may
        /// exceed <see cref="int.MaxValue"/>, so consume via a pointer-backed reader, not a single Span.
        /// </summary>
        public byte* DataPtr => dataPtr;
        public long Size => size;

        public void Dispose()
        {
            if (adviseDontNeedOnDispose && OperatingSystem.IsLinux())
            {
                // MADV_DONTNEED on a file-backed shared mapping drops the pages from the kernel
                // page cache, so it also affects the arena's global random-access view (and any
                // other mmap of the same file). Intentional: the whole-read session has finished
                // sweeping the range and we want those pages out of cache rather than competing
                // with the random-access working set. Rounds to full pages around the data range.
                nuint pageSize = PageSize;
                nuint addr = (nuint)dataPtr;
                nuint start = (addr + pageSize - 1) & ~(pageSize - 1);
                nuint end = (addr + (nuint)size) & ~(pageSize - 1);
                if (end > start)
                    Madvise((byte*)start, end - start, MADV_DONTNEED);
            }
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Mark this file as "preserve on disk when its refcount hits zero". Set by
    /// <see cref="ArenaReservation.PersistOnShutdown"/> via the snapshot's shutdown path
    /// so this session's persisted snapshots survive across restarts. Idempotent.
    /// </summary>
    public void PersistOnShutdown() => Interlocked.Exchange(ref _preserveOnDispose, 1);

    protected override void CleanUp()
    {
        CloseMmap();
        _handle.Dispose();
        // Preserve the on-disk file iff someone explicitly opted in via PersistOnShutdown;
        // otherwise delete it (the normal post-prune cleanup path).
        if (Volatile.Read(ref _preserveOnDispose) == 0)
        {
            try { File.Delete(Path); } catch { /* best-effort */ }
        }
    }
}
