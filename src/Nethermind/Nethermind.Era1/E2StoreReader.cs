// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using CommunityToolkit.HighPerformance;
using DotNetty.Buffers;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Crypto;
using Snappier;

namespace Nethermind.Era1;

public class E2StoreReader : IDisposable
{
    internal const int HeaderSize = 8;
    internal const int ValueSizeLimit = 1024 * 1024 * 50;

    private readonly SafeFileHandle _file;

    // Read these two value ahead of time instead of fetching the value everything it is needed to reduce
    // the page fault when looking up.
    private long? _startBlock;
    private long _blockCount;
    private readonly long _fileLength;
    private readonly IByteBufferAllocator _bufferAllocator = PooledByteBufferAllocator.Default;

    public E2StoreReader(string filePath): this(File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
    }

    public E2StoreReader(SafeFileHandle file)
    {
        _file = file;
        _fileLength = RandomAccess.GetLength(_file);
    }

    public (T, long) ReadEntryAndDecode<T>(long position, Func<IByteBuffer, T> decoder, ushort expectedType)
    {
        Entry entry = ReadEntry(position, expectedType);

        IByteBuffer buffer = _bufferAllocator.Buffer((int)entry.Length);
        try
        {
            ReadToByteBuffer(buffer, position + HeaderSize, (int)entry.Length);
            return (decoder(buffer), (long)(entry.Length + HeaderSize));
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
            return ((T, long))(decoder.Invoke(buffer), entry.Length + HeaderSize);
        }
        finally
        {
            buffer.Release();
        }
    }

    public Entry ReadEntry(long position, ushort? expectedType, CancellationToken token = default)
    {
        ushort type = ReadUInt16(position);
        uint length = ReadUInt32(position + 2);
        ushort reserved = ReadUInt16(position + 6);

        Entry entry = new Entry(type, length);
        if (expectedType.HasValue && entry.Type != expectedType) throw new EraException($"Expected an entry of type {expectedType}, but got {entry.Type}.");
        if (entry.Length + (ulong)position > (ulong)_fileLength)
            throw new EraFormatException($"Entry has an invalid length of {entry.Length} at position {position}, which is longer than stream length of {_fileLength}.");
        if (entry.Length > ValueSizeLimit)
            throw new EraException($"Entry exceeds the maximum size limit of {ValueSizeLimit}. Entry is {entry.Length}.");
        if (reserved != 0)
            throw new EraFormatException($"Reserved header bytes has invalid values at position {position}.");
        return entry;
    }

    private async Task<IByteBuffer> ReadEntryValueAsSnappy(long offset, ulong length, CancellationToken cancellation = default)
    {
        IByteBuffer buffer = _bufferAllocator.Buffer((int)(length * 2));
        buffer.EnsureWritable((int)length * 2, true);

        // Using _mappedFile.CreateViewStream results in crashes when things got fast enough.
        IByteBuffer inputBuffer = _bufferAllocator.Buffer((int)length);
        ReadToByteBuffer(inputBuffer, offset, (int)length);
        Stream inputStream = inputBuffer.Array.AsMemory()[inputBuffer.ArrayOffset..(inputBuffer.ArrayOffset + (int)length)].AsStream();

        using SnappyStream decompressor = new(inputStream, CompressionMode.Decompress, true);

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

        inputBuffer.Release();
        return buffer;
    }

    public void Dispose()
    {
        _file.Dispose();
    }

    public long BlockOffset(long blockNumber)
    {
        EnsureIndexAvailable();

        if (blockNumber > _startBlock + _blockCount || blockNumber < _startBlock)
            throw new ArgumentOutOfRangeException(nameof(blockNumber), $"Block {blockNumber} is outside the bounds of this index.");

        // 8 * <offset> + <count>
        int indexLength = 8 * (int)_blockCount + 8;
        long offsetLocation = indexLength - (long)(blockNumber - _startBlock!) * 8;

        // <header> + <start block> + <the rest of the index>
        int sizeIncludingHeader = HeaderSize + 8 + indexLength;

        long relativeOffset = ReadInt64(_fileLength - offsetLocation);
        return _fileLength - sizeIncludingHeader + relativeOffset;
    }

    private void EnsureIndexAvailable()
    {
        if (_startBlock != null) return;

        if (_fileLength < 32) throw new EraFormatException("Invalid era file. Too small to contain index.");

        // Read the block count
        _blockCount = (long)ReadUInt64(_fileLength - 8);

        // <starting block> + 8 * <offset> + <count>
        int indexLength = 8 + 8 * (int)_blockCount + 8;

        // Verify that its a block index
        _ = ReadEntry(_fileLength - indexLength - HeaderSize, EntryTypes.BlockIndex);

        _startBlock = (long?)ReadUInt64(_fileLength - indexLength);
    }

    public long First
    {
        get
        {
            EnsureIndexAvailable();
            return _startBlock!.Value;
        }
    }

    public long LastBlock => First + _blockCount - 1;

    public long AccumulatorOffset
    {
        get
        {
            EnsureIndexAvailable();

            // <index header> + <starting block> + 8 * <offset> + <count>
            int indexLength = 8 + 8 + 8 * (int)_blockCount + 8;

            // <header> + <the 32 byte hash> + <indexes>
            int accumulatorFromLast = E2StoreWriter.HeaderSize + 32 + indexLength;

            return _fileLength - accumulatorFromLast;
        }
    }

    public long BlockCount
    {
        get
        {
            EnsureIndexAvailable();
            return _blockCount;
        }
    }

    public ValueHash256 CalculateChecksum()
    {
        // Note: Don't close the stream
        FileStream fileStream = new FileStream(_file, FileAccess.Read);
        using SHA256 sha = SHA256.Create();
        return new ValueHash256(sha.ComputeHash(fileStream));
    }

    private ushort ReadUInt16(long position)
    {
        Span<byte> buff = stackalloc byte[2];
        RandomAccess.Read(_file, buff, position);
        return BinaryPrimitives.ReadUInt16LittleEndian(buff);
    }

    private uint ReadUInt32(long position)
    {
        Span<byte> buff = stackalloc byte[4];
        RandomAccess.Read(_file, buff, position);
        return BinaryPrimitives.ReadUInt32LittleEndian(buff);
    }

    private long ReadInt64(long position)
    {
        Span<byte> buff = stackalloc byte[8];
        RandomAccess.Read(_file, buff, position);
        return BinaryPrimitives.ReadInt64LittleEndian(buff);
    }

    private ulong ReadUInt64(long position)
    {
        Span<byte> buff = stackalloc byte[8];
        RandomAccess.Read(_file, buff, position);
        return BinaryPrimitives.ReadUInt64LittleEndian(buff);
    }

    private void ReadToByteBuffer(IByteBuffer buffer, long position, int length)
    {
        RandomAccess.Read(_file, buffer.Array.AsSpan().Slice(buffer.ArrayOffset, length), position);
        buffer.SetWriterIndex(buffer.WriterIndex + length);
    }
}
