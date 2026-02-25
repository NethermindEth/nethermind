// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A single append-only arena file for storing persisted snapshot HSST data.
/// Reads use a read-only mmap for zero-copy access; writes go through a
/// <see cref="FileStream"/> seeked to the target offset.
/// </summary>
public sealed unsafe class ArenaFile : IDisposable
{
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

    public void Dispose()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
        _handle.Dispose();
    }
}
