// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using Nethermind.Core.Crypto;
using Newtonsoft.Json.Converters;
using Snappier;

namespace Nethermind.Era1;

public enum E2StoreType
{
    TypeVersion = (ushort)0x3265,
    TypeCompressedHeader = 0x03,
    TypeCompressedBody = 0x04,
    TypeCompressedReceipts = 0x05,
    TypeTotalDifficulty = 0x06,
    TypeAccumulator = 0x07,
    TypeBlockIndex = 0x3266,
}
internal class E2Store : IDisposable
{
    internal const UInt16 TypeVersion = 0x3265;
    internal const UInt16 TypeCompressedHeader = 0x03;
    internal const UInt16 TypeCompressedBody = 0x04;
    internal const UInt16 TypeCompressedReceipts = 0x05;
    internal const UInt16 TypeTotalDifficulty = 0x06;
    internal const UInt16 TypeAccumulator = 0x07;
    internal const UInt16 TypeBlockIndex = 0x3266;
    internal const UInt16 MaxEra1Size = 8192;

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

    internal static async Task<E2Store> FromStream(Stream stream, CancellationToken token = default)
    {
        E2Store e = new E2Store(stream);
        e.Metadata = await e.ReadFileMetaData(token);
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
            string[] parts = file.Split(new char[] { '-' });
            if (parts.Length != 3 || parts[0] != network)
            {
                continue;
            }
            uint epoch;
            if (!uint.TryParse(parts[1], out epoch))
                throw new EraException($"Invalid era1 filename: {Path.GetFileName(file)}");
            else if (epoch != 0)
                throw new EraException($"Epoch {epoch} is missing.");

            next++;
            files.Add(file);    
        }
        return files;
    }

    public int WriteEntry(UInt16 type, byte[] bytes)
    {
        var buf = new byte[HeaderSize];
        buf[0] = (byte)type;
        buf[1] = (byte)(type >> 8);
        int length = bytes.Length;
        buf[2] = (byte)(length);
        buf[3] = (byte)(length >> 8);
        buf[4] = (byte)(length >> 16);
        buf[5] = (byte)(length >> 24);
        var memStream = new MemoryStream();
        var binaryWriter = new BinaryWriter(memStream);
        binaryWriter.Write(buf);
        binaryWriter.Write(bytes);
        //TODO not correct
        Debug.WriteLine(BitConverter.ToString(memStream.ToArray()));
        return bytes.Length + HeaderSize;
    }

    private async Task<EraMetadata> ReadFileMetaData(CancellationToken token = default)
    {
        long l = _stream!.Length;

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
    internal async Task<HeaderData> ReadMetadataAt(long offset, CancellationToken token = default)
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

    internal async Task<(int, Entry)> Find(ushort type, CancellationToken token = default)
    {
        var off = 0L;
        while (true)
        {
            HeaderData hd = await ReadMetadataAt(off, token);
            if (hd.Type == type)
                return await ReadEntryAt(off);

            off += HeaderSize + (int)hd.Length;
        }
    }

    internal async Task<IEnumerable<Entry>> FindAll(UInt16 type, CancellationToken token  = default)
    {
        var off = 0L;
        var entries = new List<Entry>();    
        while (true)
        {
            var hd = await ReadMetadataAt(off, token);

            if (hd.Type == type)
            {
                var (_, e)= await ReadEntryAt(off, token);
                entries.Add(e);

            }
            off += HeaderSize + hd.Length;
            if (_stream!.Length < off)
                //TODO improve message
                throw new EraException($"Malformed era1 format detected "); 
            if (_stream.Length == off)
                return entries;
        }
    }

    internal async Task<long> ReadValueAt(long off, CancellationToken token = default)
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
    internal async Task<(int, Entry)> ReadEntryAt(long off, CancellationToken token = default)
    {
        var eHeader = await ReadMetadataAt(off, token);
        
        var e = new Entry(eHeader.Type);

        if (eHeader.Length > ValueSizeLimit)
            throw new EraException($"Entry exceeds the maximum size limit of {ValueSizeLimit}. Entry is {eHeader.Length}.");
        if (eHeader.Length == 0)
            //Empty entry?
            return (HeaderSize, e);

        e.Value = new byte[eHeader.Length];
        _stream!.Position = off + HeaderSize;
        var read = await _stream.ReadAsync(e.Value, 0, e.Value.Length, token);

        if (read != eHeader.Length)
        {
            //TODO not correct length, entry mismatch 
        }

        return (HeaderSize + (int)eHeader.Length, e);
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
    public long Start;
    public long Count;
    public long Length;

    public EraMetadata(long start, long count, long length)
    {
        Start = start;
        Count = count;
        Length = length;
    }
}
