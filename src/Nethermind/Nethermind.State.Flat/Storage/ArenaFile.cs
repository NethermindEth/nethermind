// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A single append-only arena file for storing persisted snapshot HSST data.
/// Reads use a read-only mmap for zero-copy access; writes go through a
/// <see cref="FileStream"/> seeked to the target offset.
///
/// <para>
/// Lifecycle is refcounted: the owning <see cref="ArenaManager"/>'s dictionary entry
/// holds the initial lease (count 1). Each <see cref="ArenaReservation"/> referencing
/// the file holds an additional lease. The manager drops its lease via <see cref="Dispose"/>
/// (typically through <see cref="ArenaManager.MarkDead"/> or <see cref="ArenaManager.CancelWrite"/>);
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
    private const int POSIX_FADV_DONTNEED = 4;
    private static readonly nuint PageSize = (nuint)Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(void* addr, nuint length, int advice);

    [DllImport("libc", EntryPoint = "posix_fadvise", SetLastError = true)]
    private static extern int PosixFadvise(int fd, long offset, long len, int advice);

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

    public ArenaFile(int id, string path, long mappedSize)
    {
        Id = id;
        Path = path;
        MappedSize = mappedSize;

        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Extend file to mappedSize if smaller (sparse on Linux via ftruncate)
        if (RandomAccess.GetLength(_handle) < mappedSize)
            RandomAccess.SetLength(_handle, mappedSize);

        OpenMmap(mappedSize);
    }

    /// <summary>
    /// Try to acquire a lease without throwing on a disposing file. Returns false when the
    /// file is already in cleanup. Wraps the protected <see cref="RefCountingDisposable.TryAcquireLease"/>.
    /// </summary>
    internal new bool TryAcquireLease() => base.TryAcquireLease();

    public ReadOnlySpan<byte> GetSpan(long offset, long size) =>
        // Span<T> is intrinsically int-bounded; a single GetSpan can't materialise a
        // >2 GiB region. Use OpenWholeView for chunk-aware whole-reservation access
        // once that path is widened to long.
        new(_basePtr + offset, checked((int)size));

    /// <summary>
    /// Create a write stream backed by a <see cref="FileStream"/> seeked to <paramref name="startOffset"/>.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    public FileStream CreateWriteStream(long startOffset)
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

        // Round offset up to page boundary, round end down — only advise full pages
        nuint pageSize = PageSize;
        nuint start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        if (end <= start) return;

        Madvise(_basePtr + start, end - start, MADV_DONTNEED);
    }

    /// <summary>
    /// Pre-fault the page-aligned subrange by issuing a one-byte
    /// <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> per page through the
    /// file handle. The bytes land in the kernel page cache without faulting them into our
    /// process resident set; the next mmap access takes only a minor fault. Cross-platform.
    /// </summary>
    public void PopulateRead(long offset, long size)
    {
        nuint pageSize = PageSize;
        nuint start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        if (end <= start) return;

        Span<byte> oneByte = stackalloc byte[1];
        for (nuint p = start; p < end; p += pageSize)
            RandomAccess.Read(_handle, oneByte, (long)p);
    }

    /// <summary>
    /// posix_fadvise(POSIX_FADV_DONTNEED) on the underlying file descriptor for the
    /// page-aligned subrange of <c>[offset, offset+size)</c>. Drops the corresponding
    /// pages from the OS file cache. Redundant with <see cref="AdviseDontNeed"/> on
    /// Linux for shared mappings, but useful for benchmarking to ensure arena pages
    /// don't pollute the file cache.
    /// </summary>
    public void FadviseDontNeed(long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;

        nuint pageSize = PageSize;
        nuint start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        if (end <= start) return;

        int fd = (int)_handle.DangerousGetHandle();
        PosixFadvise(fd, (long)start, (long)(end - start), POSIX_FADV_DONTNEED);
    }

    /// <summary>
    /// Open a fresh per-reservation mmap view over <c>[offset, offset+size)</c> with
    /// <c>MADV_NORMAL</c> hint, distinct from the global random-access view used by point
    /// queries. Disposing the returned view applies <c>MADV_DONTNEED</c> to the range.
    /// </summary>
    public IArenaWholeView OpenWholeView(long offset, long size)
    {
        MemoryMappedViewAccessor accessor = _mmf.CreateViewAccessor(offset, size, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        // The accessor's pointer is offset by an internal page-aligned skew; add it
        // so the span starts at the requested offset's first byte.
        byte* dataPtr = ptr + accessor.PointerOffset;
        if (OperatingSystem.IsLinux())
            Madvise(dataPtr, (nuint)size, MADV_NORMAL);
        return new MmapWholeView(accessor, dataPtr, size);
    }

    private sealed unsafe class MmapWholeView(
        MemoryMappedViewAccessor accessor, byte* dataPtr, long size) : IArenaWholeView
    {
        public byte* DataPtr => dataPtr;
        public long Size => size;

        public void Dispose()
        {
            if (OperatingSystem.IsLinux())
            {
                // Round to full pages around the data range.
                // NOTE: MADV_DONTNEED on a file-backed shared mapping drops the affected
                // pages from the kernel page cache, so it also affects the arena's global
                // random-access view (and any other independent mmap of the same file).
                // That's intentional here — the whole-read session has finished sweeping
                // the range and we want those pages out of cache rather than competing
                // with the random-access working set.
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
