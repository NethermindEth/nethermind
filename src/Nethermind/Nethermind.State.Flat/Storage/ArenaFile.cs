// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private const int MADV_DONTNEED = 4;
    private static readonly nuint PageSize = (nuint)Environment.SystemPageSize;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(void* addr, nuint length, int advice);

    private readonly SafeFileHandle _handle;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePtr;

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
    }

    public ReadOnlySpan<byte> GetSpan(long offset, int size) =>
        new(_basePtr + offset, size);

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

    public void AdviseDontNeed(long offset, int size)
    {
        if (!OperatingSystem.IsLinux()) return;

        // Round offset up to page boundary, round end down — only advise full pages
        nuint pageSize = PageSize;
        nuint start = ((nuint)offset + pageSize - 1) & ~(pageSize - 1);
        nuint end = ((nuint)offset + (nuint)size) & ~(pageSize - 1);
        if (end <= start) return;

        Madvise(_basePtr + start, end - start, MADV_DONTNEED);
    }

    public void Dispose()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
        _handle.Dispose();
    }
}
