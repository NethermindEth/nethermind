// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.MemoryMappedFiles;

namespace Nethermind.State.Flat.Io;

/// <summary>Read-only whole-file mmap, serving as both the reader source and the reader.</summary>
/// <remarks>
/// For standalone files that are not arena-managed — a transient sorted run, for instance. The mapping
/// covers the file as it was at construction, so the file must not be written or truncated while this
/// instance is alive.
/// <para>Unlike the arena readers this is a class rather than a <c>ref struct</c>, so callers may hold
/// one per source in an array and use it from an iterator; the pointer is owned here and stays valid
/// until <see cref="Dispose"/>.</para>
/// </remarks>
internal sealed unsafe class MappedByteFile : IByteReaderSource<MappedByteFile, NoOpPin>, IByteReader<NoOpPin>, IDisposable
{
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

    public MappedByteFile CreateReader() => this;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset + (ulong)output.Length > (ulong)Length) return false;
        new ReadOnlySpan<byte>(_basePtr + offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(Bound bound)
    {
        if ((ulong)bound.Offset + (ulong)bound.Length > (ulong)Length)
            throw new ArgumentOutOfRangeException(nameof(bound));
        return new NoOpPin(new ReadOnlySpan<byte>(_basePtr + bound.Offset, checked((int)bound.Length)));
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
