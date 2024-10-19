// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using DotNetty.Buffers;
using Snappier;

namespace Nethermind.Era1;

public class E2StoreReader : IDisposable
{
    internal const int HeaderSize = 8;
    internal const int ValueSizeLimit = 1024 * 1024 * 50;

    private MemoryMappedFile _mappedFile;
    private MemoryMappedViewAccessor _accessor;
    private IByteBufferAllocator _bufferAllocator = PooledByteBufferAllocator.Default;

    public E2StoreReader(MemoryMappedFile mmf)
    {
        _mappedFile = mmf;
        _accessor = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public E2StoreReader(string filename): this(MemoryMappedFile.CreateFromFile(filename, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
    {
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

        long relativeOffset = _accessor.ReadInt64(_accessor.Capacity - offsetLocation);
        return _accessor.Capacity - sizeIncludingHeader + relativeOffset;
    }

    private void EnsureIndexAvailable()
    {
        if (_startBlock != null) return;

        // Read the block count
        _blockCount = _accessor.ReadInt64(_accessor.Capacity - 8);

        // <starting block> + 8 * <offset> + <count>
        int indexLength = 8 + 8 * (int)_blockCount + 8;

        _startBlock = _accessor.ReadInt64(_accessor.Capacity - indexLength);
    }

    // Read these two value ahead of time instead of fetching the value everything it is needed to reduce
    // the page fault when looking up.
    private long? _startBlock;
    private long _blockCount;

    public long StartBlock
    {
        get
        {
            EnsureIndexAvailable();
            return _startBlock!.Value;
        }
    }

    public long LastBlock => StartBlock + _blockCount - 1;

    public long AccumulatorOffset
    {
        get
        {
            EnsureIndexAvailable();

            // <index header> + <starting block> + 8 * <offset> + <count>
            int indexLength = 8 + 8 + 8 * (int)_blockCount + 8;

            // <header> + <the 32 byte hash> + <indexes>
            int accumulatorFromLast = E2StoreWriter.HeaderSize + 32 + indexLength;

            return _accessor.Capacity - accumulatorFromLast;
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

}
