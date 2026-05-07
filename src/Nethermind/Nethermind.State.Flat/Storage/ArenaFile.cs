// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A single append-only arena file for storing persisted snapshot HSST data.
/// Reads use a read-only mmap for zero-copy access; writes go through a
/// <see cref="FileStream"/> seeked to the target offset.
/// </summary>
public sealed unsafe class ArenaFile : IDisposable
{
    private const int MADV_NORMAL = 0;
    private const int MADV_RANDOM = 1;
    private const int MADV_DONTNEED = 4;
    private const int MADV_POPULATE_READ = 22;
    private const int POSIX_FADV_DONTNEED = 4;
    private static readonly nuint PageSize = (nuint)Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(void* addr, nuint length, int advice);

    [DllImport("libc", EntryPoint = "posix_fadvise", SetLastError = true)]
    private static extern int PosixFadvise(int fd, long offset, long len, int advice);

    private readonly SafeFileHandle _handle;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePtr;

    /// <summary>Raw pointer to the first byte of the arena's mmap. Long-offset arithmetic OK across the full <see cref="MappedSize"/>.</summary>
    public byte* BasePtr => _basePtr;

    public int Id { get; }
    public string Path { get; }
    public long MappedSize { get; }

    public ArenaFile(int id, string path, long mappedSize)
    {
        Id = id;
        Path = path;
        MappedSize = mappedSize;

        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Extend file to mappedSize if smaller (sparse on Linux via ftruncate)
        if (RandomAccess.GetLength(_handle) < mappedSize)
            RandomAccess.SetLength(_handle, mappedSize);

        _mmf = MemoryMappedFile.CreateFromFile(_handle, mapName: null, mappedSize, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _accessor = _mmf.CreateViewAccessor(0, mappedSize, MemoryMappedFileAccess.Read);

        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

        if (OperatingSystem.IsLinux())
            Madvise(_basePtr, (nuint)mappedSize, MADV_RANDOM);
    }

    public ReadOnlySpan<byte> GetSpan(long offset, long size) =>
        // Span<T> is intrinsically int-bounded; a single GetSpan can't materialise a
        // >2 GiB region. Use OpenWholeView for chunk-aware whole-reservation access
        // once that path is widened to long.
        new(_basePtr + offset, checked((int)size));

    public byte[] Read(long offset, int size) =>
        GetSpan(offset, size).ToArray();

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

    public void Touch(long offset, long size)
    {
        if (size <= 0) return;
        byte[] buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long end = offset + size;
            while (offset < end)
            {
                int chunk = (int)Math.Min(buf.Length, end - offset);
                int read = RandomAccess.Read(_handle, buf.AsSpan(0, chunk), offset);
                if (read <= 0) break;
                offset += read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
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
    /// madvise(MADV_POPULATE_READ) on the page-aligned subrange. On Linux ≥ 5.14 the kernel
    /// pre-faults the pages so the next read does not block on a page fault. On older kernels
    /// the call returns EINVAL, which is benign and ignored.
    /// </summary>
    public void PopulateRead(long offset, long size)
    {
        if (!OperatingSystem.IsLinux()) return;

        nuint pageSize = PageSize;
        nuint start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        if (end <= start) return;

        Madvise(_basePtr + start, end - start, MADV_POPULATE_READ);
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
        // Span<T> is int-bounded; for >2 GiB views callers should use DataPtr + Size
        // (or a reader built on top of them) instead of GetSpan.
        public ReadOnlySpan<byte> GetSpan() => new(dataPtr, checked((int)size));

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

    public void Dispose()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
        _handle.Dispose();
    }
}
