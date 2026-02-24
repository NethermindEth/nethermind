// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A single append-only arena file for storing persisted snapshot HSST data,
/// backed by a memory-mapped file for zero-copy I/O.
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

        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        // Extend file to mappedSize if smaller (sparse on Linux via ftruncate)
        if (RandomAccess.GetLength(_handle) < mappedSize)
            RandomAccess.SetLength(_handle, mappedSize);

        _mmf = MemoryMappedFile.CreateFromFile(_handle, mapName: null, mappedSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        _accessor = _mmf.CreateViewAccessor(0, mappedSize, MemoryMappedFileAccess.ReadWrite);

        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);
    }

    public Span<byte> GetSpan(long offset, int size) =>
        new(_basePtr + offset, size);

    public void Write(long offset, ReadOnlySpan<byte> data) =>
        data.CopyTo(GetSpan(offset, data.Length));

    public byte[] Read(long offset, int size) =>
        GetSpan(offset, size).ToArray();

    public void Dispose()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
        _handle.Dispose();
    }
}
