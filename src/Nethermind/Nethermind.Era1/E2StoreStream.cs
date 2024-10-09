// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Snappier;
namespace Nethermind.Era1;

internal class E2StoreStream : IDisposable
{
    internal const int HeaderSize = 8;
    internal const int ValueSizeLimit = 1024 * 1024 * 50;

    private readonly Stream _stream;
    private bool _disposedValue;
    private IByteBufferAllocator _bufferAllocator;
    private MemoryStream? _compressedData;

    public long StreamLength => _stream.Length;

    public long Position => _stream.Position;

    public Task<EraMetadata> GetMetadata(CancellationToken token)
    {
        return EraMetadata.CreateEraMetadata(_stream, token);
    }

    public static E2StoreStream ForWrite(Stream stream)
    {
        if (!stream.CanWrite)
            throw new ArgumentException("Stream must be writeable.", nameof(stream));
        return new(stream);
    }
    public static Task<E2StoreStream> ForRead(Stream stream, IByteBufferAllocator? bufferAllocator, CancellationToken cancellation)
    {
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        E2StoreStream storeStream = new(stream, bufferAllocator);
        return Task.FromResult(storeStream);
    }
    internal E2StoreStream(Stream stream, IByteBufferAllocator? bufferAllocator = null)
    {
        _stream = stream;
        _bufferAllocator = bufferAllocator ?? PooledByteBufferAllocator.Default;
    }

    public Task<int> WriteEntryAsSnappy(UInt16 type, Memory<byte> bytes, CancellationToken cancellation = default)
    {
        return WriteEntry(type, bytes, true, cancellation);
    }
    public Task<int> WriteEntry(UInt16 type, Memory<byte> bytes, CancellationToken cancellation = default)
    {
        return WriteEntry(type, bytes, false, cancellation);
    }

    private async Task<int> WriteEntry(UInt16 type, Memory<byte> bytes, bool asSnappy, CancellationToken cancellation = default)
    {
        using ArrayPoolList<byte> headerBuffer = new(HeaderSize);
        //See https://github.com/google/snappy/blob/main/framing_format.txt
        if (asSnappy && bytes.Length > 0)
        {
            //TODO find a way to write directly to file, and still return the number of bytes written
            EnsureCompressedStream(bytes.Length);

            using SnappyStream compressor = new(_compressedData!, CompressionMode.Compress, true);

            await compressor!.WriteAsync(bytes, cancellation);
            await compressor.FlushAsync();

            bytes = _compressedData!.ToArray();
        }

        headerBuffer.Add((byte)type);
        headerBuffer.Add((byte)(type >> 8));
        int length = bytes.Length;
        headerBuffer.Add((byte)(length));
        headerBuffer.Add((byte)(length >> 8));
        headerBuffer.Add((byte)(length >> 16));
        headerBuffer.Add((byte)(length >> 24));
        headerBuffer.Add(0);
        headerBuffer.Add(0);

        ReadOnlyMemory<byte> headerMemory = headerBuffer.AsReadOnlyMemory(0, HeaderSize);
        await _stream.WriteAsync(headerMemory, cancellation);
        if (length > 0)
        {
            await _stream.WriteAsync(bytes, cancellation);
        }

        return length + HeaderSize;
    }

    public async Task<T> ReadEntryAndDecode<T>(Func<IByteBuffer, T> decoder, ushort expectedType, CancellationToken token = default)
    {
        Entry entry = await ReadEntry(expectedType, token);
        if (_stream.Position + entry.Length > StreamLength) throw new EraFormatException($"Entry has a length ({entry.Length}) and offset ({_stream.Position}) that would read beyond the length of the stream.");

        IByteBuffer buffer = _bufferAllocator.Buffer((int)entry.Length);
        try
        {
            await buffer.WriteBytesAsync(_stream, (int)entry.Length, token);
            return decoder.Invoke(buffer);
        }
        finally
        {
            buffer.Release();
        }
    }

    public async Task<T> ReadSnappyCompressedEntryAndDecode<T>(Func<IByteBuffer, T> decoder, ushort expectedType, CancellationToken token = default)
    {
        Entry entry = await ReadEntry(expectedType, token);

        IByteBuffer buffer = await ReadEntryValueAsSnappy(entry, token);
        try
        {
            return decoder.Invoke(buffer);
        }
        finally
        {
            buffer.Release();
        }
    }

    public async Task<Entry> ReadEntry(ushort? expectedType, CancellationToken token = default)
    {
        var buf = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            var read = await _stream.ReadAsync(buf.AsMemory(0, HeaderSize), token);
            if (read != HeaderSize)
                throw new EraFormatException($"Entry header could not be read at position {_stream.Position}.");
            Entry entry = new Entry(BitConverter.ToUInt16(buf, 0), BitConverter.ToUInt32(buf, 2));
            if (expectedType.HasValue && entry.Type != expectedType) throw new EraException($"Expected an entry of type {expectedType}, but got {entry.Type}.");
            if (entry.Length + _stream.Position > StreamLength)
                throw new EraFormatException($"Entry has an invalid length of {entry.Length} at position {_stream.Position}, which is longer than stream length of {StreamLength}.");
            if (entry.Length > ValueSizeLimit)
                throw new EraException($"Entry exceeds the maximum size limit of {ValueSizeLimit}. Entry is {entry.Length}.");
            if (buf[6] != 0 || buf[7] != 0)
                throw new EraFormatException($"Reserved header bytes has invalid values at position {_stream.Position}.");
            return entry;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private async Task<IByteBuffer> ReadEntryValueAsSnappy(Entry e, CancellationToken cancellation = default)
    {
        if (_stream.Position + e.Length > StreamLength) throw new EraFormatException($"Entry has a length ({e.Length}) and offset ({_stream.Position}) that would read beyond the length of the stream.");

        IByteBuffer buffer = _bufferAllocator.Buffer((int)(e.Length * 4));
        buffer.EnsureWritable((int)e.Length * 4, true);

        using StreamSegment streamSegment = new(_stream, _stream.Position, e.Length);
        using SnappyStream decompressor = new(streamSegment, CompressionMode.Decompress, true);

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
            //We don't know the uncompressed length
            await buffer.WriteBytesAsync(decompressor, buffer.WritableBytes, cancellation);
            read = buffer.WriterIndex - before;
        }
        while (read != 0);

        return buffer;
    }

    private void EnsureCompressedStream(int minLength)
    {
        if (_compressedData == null)
            _compressedData = new MemoryStream(minLength);
        else
            _compressedData.SetLength(0);
    }

    public Task Flush(CancellationToken cancellation = default)
    {
        return _stream.FlushAsync(cancellation);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _stream?.Dispose();
                _compressedData?.Dispose();
            }
            _disposedValue = true;
        }
    }
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    internal long Seek(long offset, SeekOrigin origin)
    {
        return _stream.Seek(offset, origin);
    }
}
