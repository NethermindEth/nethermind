// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Text;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Newtonsoft.Json.Linq;
using Snappier;
namespace Nethermind.Era1;

internal class E2Store : IDisposable
{
    internal const int HeaderSize = 8;
    internal const int ValueSizeLimit = 1024 * 1024 * 50;

    private Stream _stream;
    private bool _disposedValue;

    private EraMetadata? _metadata;
    public long StreamLength => _stream.Length;
    public EraMetadata Metadata => GetMetadata();

    public E2Store(Stream stream)
    {
        _stream = stream;
    }

    private EraMetadata GetMetadata()
    {
        if (_metadata is null)
        {
            _metadata = ReadEraMetaData().GetAwaiter().GetResult();
        }
        return _metadata;
    }

    public async Task SetMetaData(CancellationToken token = default)
    {
        _metadata = await ReadEraMetaData(token);
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
            using MemoryStream memoryStream = new MemoryStream();
            using SnappyStream snappyStream = new(memoryStream, CompressionMode.Compress, true);
            await snappyStream.WriteAsync(bytes, cancellation);
            await snappyStream.FlushAsync();
            bytes = memoryStream.ToArray();
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

        await _stream.WriteAsync(headerBuffer.AsMemory(0, HeaderSize), cancellation);
        if (length > 0) await _stream.WriteAsync(bytes, cancellation);

        return length + HeaderSize;
    }

    private async Task<EraMetadata> ReadEraMetaData(CancellationToken token = default)
    {
        long l = _stream!.Length;
        if (_stream.Length < 16)
            throw new EraFormatException($"Data is not in a valid Era format.");

        using ArrayPoolList<byte> pooledBytes = new(16);
        Memory<byte> bytes = pooledBytes.AsMemory(0, 16);

        _stream.Position = _stream.Length - 8;
        await _stream.ReadAsync(bytes.Slice(0, 8), token);
        long c = BitConverter.ToInt64(bytes.Span);

        long indexOffset = l - 16L - c * 8;
        _stream.Position = indexOffset;
        await _stream.ReadAsync(bytes.Slice(8, 8), token);
        long s = BitConverter.ToInt64(bytes.Slice(8).Span);
        return new EraMetadata(s, c, l);
    }

    // Reads the header metadata at the given offset.
    public async Task<HeaderData> ReadEntryHeaderAt(long offset, CancellationToken token = default)
    {
        CheckStreamBounds(offset);

        var buf = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            _stream!.Position = offset;
            var read = await _stream.ReadAsync(buf, 0, HeaderSize, token);
            if (read != HeaderSize)
            {
                //TODO throw wrong header length?
            }
            if (buf[6] != 0 || buf[7] != 0)
            {
                //TODO Reserved bytes are not zero ?
            }
            HeaderData h = new()
            {
                Type = BitConverter.ToUInt16(buf, 0),
                Length = BitConverter.ToUInt32(buf, 2)
            };
            if (h.Length > StreamLength - offset)
                throw new EraFormatException($"Invalid length of entry value was detected. Entry has a length of {h.Length} at position {offset}, and Stream has a length of {StreamLength}.");
            return h;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
    public async Task<IEnumerable<Entry>> FindAll(UInt16 type, CancellationToken token = default)
    {
        int off = 0;
        var entries = new List<Entry>();
        while (true)
        {
            var hd = await ReadEntryHeaderAt(off, token);

            if (hd.Type == type)
            {
                var e = await ReadEntryAt(off, token);
                entries.Add(e);

            }
            off += HeaderSize + (int)hd.Length;
            if (_stream!.Length < off)
                //TODO improve message
                throw new EraException($"Malformed era1 format detected ");
            if (_stream.Length == off)
                return entries;
        }
    }
    public async Task<long> ReadValueAt(long off, CancellationToken token = default)
    {
        CheckStreamBounds(off);

        byte[] buf = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            _stream.Position = off;
            await _stream.ReadAsync(buf, 0, 8, token);
            return BitConverter.ToInt64(buf);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
    public Task<Entry> ReadEntryCurrentPosition(CancellationToken token = default)
    {
        return ReadEntryAt(_stream.Position, token);
    }
    public async Task<Entry> ReadEntryAt(long off, CancellationToken token = default)
    {
        CheckStreamBounds(off);

        var eHeader = await ReadEntryHeaderAt(off, token);

        Entry e = new(eHeader.Type, off, eHeader.Length);

        if (eHeader.Length > ValueSizeLimit)
            throw new EraException($"Entry exceeds the maximum size limit of {ValueSizeLimit}. Entry is {eHeader.Length}.");
        if (eHeader.Length == 0)
            //Empty entry?
            return e;

        return e;
    }

    public async Task<int> ReadEntryValueAsSnappy(IByteBuffer buffer, Entry e, CancellationToken cancellation = default)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (e.ValueOffset + e.Length > StreamLength) throw new EraFormatException($"Entry has a length ({e.Length}) and offset ({e.Offset}) that would read beyond the length of the stream.");
        //TODO is this necessary?
        //buffer.EnsureWritable((int)e.Length * 2, true);

        using SnappyStream snappy = new(new StreamSegment(_stream, e.ValueOffset, e.Length), CompressionMode.Decompress, true);
        int totalRead = 0;
        int read = 0;
        do
        {
            int before = buffer.WriterIndex;
            //We don't know the uncompressed length 
            await buffer.WriteBytesAsync(snappy, (int)e.Length * 4, cancellation);
            read = buffer.WriterIndex - before;
            totalRead += read;
        }
        while (read != 0);
        return totalRead;
    }

    public async ValueTask<int> ReadEntryValue(IByteBuffer buffer, Entry e, CancellationToken cancellation = default)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (buffer.Capacity < e.Length) throw new ArgumentException($"Buffer must be at least {e.Length} long.", nameof(buffer));
        if (e.ValueOffset + e.Length > StreamLength) throw new EraFormatException($"Entry has a length ({e.Length}) and offset ({e.Offset}) that would read beyond the length of the stream.");

        _stream.Position = e.ValueOffset;
        await buffer.WriteBytesAsync(_stream, (int)e.Length);
        return (int)e.Length;
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
    private void CheckStreamBounds(long offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot be a negative number.");
        if (offset > StreamLength - 8)
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot read beyond the length of the stream.");
    }

}
