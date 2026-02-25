// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A single append-only arena file for storing persisted snapshot HSST data,
/// backed by a memory-mapped file for zero-copy I/O.
/// Writes go through a <see cref="MmapWriteStream"/> that writes directly into the
/// mmap region, so reads via <see cref="GetSpan"/> see data immediately.
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

    public ReadOnlySpan<byte> GetSpan(long offset, int size) =>
        new(_basePtr + offset, size);

    public byte[] Read(long offset, int size) =>
        GetSpan(offset, size).ToArray();

    /// <summary>
    /// Create a stream that writes directly into the mmap region at the given offset.
    /// </summary>
    public MmapWriteStream CreateWriteStream(long startOffset) =>
        new(_basePtr, startOffset, MappedSize);

    public void Dispose()
    {
        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
        _mmf.Dispose();
        _handle.Dispose();
    }

    /// <summary>
    /// A write-only stream that copies data directly into the mmap region.
    /// Reads through <see cref="ArenaFile.GetSpan"/> see the data immediately
    /// since both operate on the same virtual memory.
    /// </summary>
    public sealed class MmapWriteStream(byte* basePtr, long startOffset, long capacity) : Stream
    {
        private long _position = startOffset;

        public override void Write(byte[] buffer, int offset, int count)
        {
            new ReadOnlySpan<byte>(buffer, offset, count)
                .CopyTo(new Span<byte>(basePtr + _position, count));
            _position += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            buffer.CopyTo(new Span<byte>(basePtr + _position, buffer.Length));
            _position += buffer.Length;
        }

        public override void Flush() { } // mmap writes are immediately visible
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => capacity;
        public override long Position { get => _position; set => _position = value; }
        public override long Seek(long offset, SeekOrigin origin) =>
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => capacity + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
