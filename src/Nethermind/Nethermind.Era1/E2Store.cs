// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Newtonsoft.Json.Converters;
using Snappier;

namespace Nethermind.Era1;

internal class E2Store : IDisposable
{
    internal const int HeaderSize = 8;
    internal const int ValueSizeLimit = 1024 * 1024 * 50;

    private Stream _stream;
    private bool _disposedValue;

    public long Position => _stream!.Position;

    public EraMetadata Metadata {  get; private set; }

    private E2Store(Stream stream)
    {
        _stream = stream;        
    }

    public static Task<E2Store> ForRead(Stream stream, CancellationToken token = default) => FromStream(stream, true, token);
    public static Task<E2Store> ForWrite(Stream stream, CancellationToken token = default) => FromStream(stream, false, token);

    private static async Task<E2Store> FromStream(Stream stream, bool initForRead, CancellationToken token = default)
    {
        E2Store e = new E2Store(stream);
        if (initForRead)
            e.Metadata = await e.ReadEraMetaData(token);
        return e;
    }

    public string Filename(string network, int epoch , Keccak root )
    {
        return $"{network}-{epoch}-{root.ToString(true)[..8]}.era1"; 
    }

    // Format: <network>-<epoch>-<hexroot>.era1
    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
    {
        var entries = Directory.GetFiles(directoryPath, "*.era1");
        if (!entries.Any())
            return Array.Empty<string>();

        uint next = 0;
        List<string> files = new();

        foreach (string file in entries)
        {
            string[] parts = Path.GetFileName(file).Split(new char[] { '-' });
            if (parts.Length != 3 || parts[0] != network)
            {
                continue;
            }
            uint epoch;
            if (!uint.TryParse(parts[1], out epoch))
                throw new EraException($"Invalid era1 filename: {Path.GetFileName(file)}");
            else if (epoch != next)
                throw new EraException($"Epoch {epoch} is missing.");

            next++;
            files.Add(file);    
        }
        return files;
    }

    public Task<int> WriteEntryAsSnappy(UInt16 type, byte[] bytes, CancellationToken cancellation = default)
    {
        return WriteEntry(type, bytes, true, cancellation);
        
    }
    public Task<int> WriteEntry(UInt16 type, byte[] bytes, CancellationToken cancellation = default)
    {
        return WriteEntry(type, bytes, false, cancellation); 
    }

    private async Task<int> WriteEntry(UInt16 type, byte[] bytes, bool asSnappy, CancellationToken cancellation = default)
    {
        //TODO refactor to sync and use span?
        var headerBuffer = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            headerBuffer[0] = (byte)type;
            headerBuffer[1] = (byte)(type >> 8);
            int length = bytes.Length;
            headerBuffer[2] = (byte)(length);
            headerBuffer[3] = (byte)(length >> 8);
            headerBuffer[4] = (byte)(length >> 16);
            headerBuffer[5] = (byte)(length >> 24);
            headerBuffer[6] = 0;
            headerBuffer[7] = 0;

            await _stream.WriteAsync(headerBuffer, 0, HeaderSize, cancellation);
            long written = bytes.Length;
            if (asSnappy)
            {
                long before = _stream.Position;
                using SnappyStream snappyStream = new(_stream, CompressionMode.Compress, true);
                await snappyStream.WriteAsync(bytes, cancellation);
                written = _stream.Position - before;
            }
            else
                await _stream.WriteAsync(bytes, 0, bytes.Length, cancellation);
            return (int)written + HeaderSize;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private async Task<EraMetadata> ReadEraMetaData(CancellationToken token = default)
    {
        long l = _stream!.Length;
        if (_stream.Length < 16)
        {
            throw new EraFormatException($"Data is not in a valid Era format.");
        }

        byte[] bytes = new byte[16];
        _stream.Position = _stream.Length - 8;  
        await _stream.ReadAsync(bytes, 0, 8, token);
        long c = BitConverter.ToInt64(bytes);

        _stream.Position = l - 16L - c * 8;
        await _stream.ReadAsync(bytes, 8, 8, token);
        long s = BitConverter.ToInt64(bytes, 8);
        return new EraMetadata(s, c, l);
    }

    // Reads the header metadata at the given offset.
    public async Task<HeaderData> ReadMetadataAt(long offset, CancellationToken token = default)
    {
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
            return h;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public async Task<IEnumerable<Entry>> FindAll(UInt16 type, CancellationToken token  = default)
    {
        int off = 0;
        var entries = new List<Entry>();    
        while (true)
        {
            var hd = await ReadMetadataAt(off, token);

            if (hd.Type == type)
            {
                var (_, e)= await ReadEntryAt(off, token);
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
    public Task<(int, Entry)> ReadEntryCurrentPosition(CancellationToken token = default)
    {
        return ReadEntryAt((int)_stream.Position, token);
    }
    public async Task<(int, Entry)> ReadEntryAt(long off, CancellationToken token = default)
    {
        var eHeader = await ReadMetadataAt(off, token);
        
        Entry e = new (eHeader.Type, off, eHeader.Length);

        if (eHeader.Length > ValueSizeLimit)
            throw new EraException($"Entry exceeds the maximum size limit of {ValueSizeLimit}. Entry is {eHeader.Length}.");
        if (eHeader.Length == 0)
            //Empty entry?
            return (HeaderSize, e);

        return (HeaderSize + (int)eHeader.Length, e);
    }

    public Task<int> ReadEntryValueAsSnappy(byte[] buffer, Entry e, CancellationToken cancellation = default)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        using SnappyStream snappy = new (new StreamSegment(_stream, e.ValueOffset, e.Length), CompressionMode.Decompress, true);
        return snappy.ReadAsync(buffer, 0, buffer.Length, cancellation);
    }

    public async ValueTask<int> ReadEntryValue(byte[] buffer, Entry e, CancellationToken cancellation = default)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (buffer.Length < e.Length)
            throw new ArgumentException($"Buffer must be at least {e.Length} long.", nameof(buffer));
        _stream.Position = e.ValueOffset;
        int read = 0; 
        while (read < e.Length)
        {
            read += await _stream.ReadAsync(buffer, read, (int)e.Length, cancellation);
            if (read == 0)
                break;
        }
        if (read != e.Length)
        {
            //TODO not correct length, entry mismatch 
        }
        return read;    
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
}

internal struct HeaderData
{
    public ushort Type;
    public uint Length;
}

internal struct EraMetadata
{
    public long Start { get; }
    public long Count { get; }
    public long Length { get; }

    public EraMetadata(long start, long count, long length)
    {
        Start = start;
        Count = count;
        Length = length;
    }
}
