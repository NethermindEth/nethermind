// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using DotNetty.Buffers;
using Snappier;

namespace Nethermind.Era1;

public class EraFileReader : IDisposable
{
    internal const int HeaderSize = 8;
    internal const int ValueSizeLimit = 1024 * 1024 * 50;

    private MemoryMappedFile _mappedFile;
    private MemoryMappedViewAccessor _accessor;
    private IByteBufferAllocator _bufferAllocator = PooledByteBufferAllocator.Default;

    public EraFileReader(MemoryMappedFile mmf)
    {
        _mappedFile = mmf;
        _accessor = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public EraFileReader(string filename): this(MemoryMappedFile.CreateFromFile(filename, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
    {
    }

    public EraMetadata CreateMetadata()
    {
        return EraMetadata.CreateEraMetadata(_mappedFile);
    }

    public (T, long) ReadEntryAndDecode<T>(long position, Func<IByteBuffer, T> decoder, ushort expectedType)
    {
        Entry entry = ReadEntry(position, expectedType);

        IByteBuffer buffer = _bufferAllocator.Buffer((int)entry.Length);
        try
        {
            // Surprisingly there are no safe way to get `Memory<byte>` or `Span<byte>`.
            _accessor.ReadArray(position + HeaderSize, buffer.Array, buffer.ArrayOffset, (int)entry.Length);
            buffer.SetWriterIndex(buffer.WriterIndex + (int)entry.Length);
            return (decoder(buffer), entry.Length + HeaderSize);
        }
        finally
        {
            buffer.Release();
        }
    }

    public async Task<(T, long)> ReadSnappyCompressedEntryAndDecode<T>(long position, Func<IByteBuffer, T> decoder, ushort expectedType, CancellationToken token = default)
    {
        Entry entry = ReadEntry(position, expectedType);

        IByteBuffer buffer = await ReadEntryValueAsSnappy(position + HeaderSize, entry.Length, token);
        try
        {
            return (decoder.Invoke(buffer), entry.Length + HeaderSize);
        }
        finally
        {
            buffer.Release();
        }
    }

    public Entry ReadEntry(long position, ushort? expectedType, CancellationToken token = default)
    {
        ushort type = _accessor.ReadUInt16(position + 0);
        uint length = _accessor.ReadUInt32(position + 2);
        ushort reserved = _accessor.ReadUInt16(position + 6);

        Entry entry = new Entry(type, length);
        if (expectedType.HasValue && entry.Type != expectedType) throw new EraException($"Expected an entry of type {expectedType}, but got {entry.Type}.");
        if (entry.Length + position > _accessor.Capacity)
            throw new EraFormatException($"Entry has an invalid length of {entry.Length} at position {position}, which is longer than stream length of {_accessor.Capacity}.");
        if (entry.Length > ValueSizeLimit)
            throw new EraException($"Entry exceeds the maximum size limit of {ValueSizeLimit}. Entry is {entry.Length}.");
        if (reserved != 0)
            throw new EraFormatException($"Reserved header bytes has invalid values at position {position}.");
        return entry;
    }

    private async Task<IByteBuffer> ReadEntryValueAsSnappy(long offset, long length, CancellationToken cancellation = default)
    {
        IByteBuffer buffer = _bufferAllocator.Buffer((int)(length * 2));
        buffer.EnsureWritable((int)length * 2, true);

        // TODO: No ToArray()
        using SnappyStream decompressor = new(_mappedFile.CreateViewStream(offset, length, MemoryMappedFileAccess.Read), CompressionMode.Decompress, true);

        int read;
        do
        {
            if (buffer.WritableBytes <= 0)
            {
                IByteBuffer newBuffer = _bufferAllocator.Buffer(buffer.ReadableBytes * 2);
                newBuffer.WriteBytes(buffer);
                buffer.Release();
                buffer = newBuffer;
            }

            int before = buffer.WriterIndex;
            // We don't know the uncompressed length
            await buffer.WriteBytesAsync(decompressor, buffer.WritableBytes, cancellation);
            read = buffer.WriterIndex - before;
        }
        while (read != 0);

        return buffer;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mappedFile.Dispose();
    }
}
