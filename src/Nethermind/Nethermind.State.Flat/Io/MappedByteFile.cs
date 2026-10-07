// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Io;

/// <summary>Read-only whole-file mmap, serving as the reader source.</summary>
/// <remarks>
/// For standalone files that are not arena-managed — a transient sorted run, for instance. The mapping
/// covers the file as it was at construction, so the file must not be written or truncated while this
/// instance is alive.
/// <para>Unlike the arena readers this is a class rather than a <c>ref struct</c>, so callers may hold
/// one per source in an array and use it from an iterator; the pointer is owned here and stays valid
/// until <see cref="Dispose"/>.</para>
/// </remarks>
internal sealed unsafe class MappedByteFile : IByteReaderSource<WholeReadSessionReader, NoOpPin>, IDisposable
{
    private const int MADV_SEQUENTIAL = 2;

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(void* addr, nuint length, int advice);

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _basePtr;
    private bool _disposed;

    public MappedByteFile(string path)
    {
        Length = new FileInfo(path).Length;
        if (Length == 0) throw new InvalidDataException($"Cannot map the empty file {path}.");
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        try
        {
            _view = _file.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);
        }
        catch
        {
            _view?.Dispose();
            _file.Dispose();
            throw;
        }
    }

    public long Length { get; }

    public WholeReadSessionReader CreateReader() => new(_basePtr, Length);

    /// <summary>Hints the kernel that the mapping will be read front to back, widening its readahead.</summary>
    public void AdviseSequential()
    {
        if (OperatingSystem.IsLinux()) Madvise(_basePtr, (nuint)Length, MADV_SEQUENTIAL);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
        _basePtr = null;
    }
}
