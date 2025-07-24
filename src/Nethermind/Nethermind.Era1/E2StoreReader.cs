// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using CommunityToolkit.HighPerformance;
using DotNetty.Buffers;
using Microsoft.IO;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Snappier;

namespace Nethermind.Era1;

public class E2StoreReader : IDisposable
{
    private const int HeaderSize = 8;
    private const int IndexSectionCount = 8;
    private const int IndexSectionStartBlock = 8;
    private const int IndexOffsetSize = 8;
    private const int ValueSizeLimit = 1024 * 1024 * 50;

    private readonly SafeFileHandle _file;

    // Read these two value ahead of time instead of fetching the value everything it is needed to reduce
    // the page fault when looking up.
    private long? _startBlock;
    private long _blockCount;
    private readonly long _fileLength;

    public E2StoreReader(string filePath)
    {
        _file = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _fileLength = RandomAccess.GetLength(_file);
    }

    public long ReadEntryAndDecode<T>(long position, Func<Memory<byte>, T> decoder, ushort expectedType, out T value)
    {
        Entry entry = ReadEntry(position, expectedType);

        int length = (int)entry.Length;
        using ArrayPoolList<byte> buffer = new ArrayPoolList<byte>(length, length);
        RandomAccess.Read(_file, buffer.AsSpan(), position + HeaderSize);
        value = decoder(buffer.AsMemory());
        return (long)(entry.Length + HeaderSize);
    }

    public async Task<(T, long)> ReadSnappyCompressedEntryAndDecode<T>(long position, Func<Memory<byte>, T> decoder, ushort expectedType, CancellationToken token = default)
    {
        Entry entry = ReadEntry(position, expectedType);
        T returnValue = await ReadEntryValueAsSnappy(position + HeaderSize, entry.Length, decoder, token);
        return ((T, long))(returnValue, entry.Length + HeaderSize);
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

    private async Task<T> ReadEntryValueAsSnappy<T>(long offset, ulong length, Func<Memory<byte>, T> decoder, CancellationToken cancellation = default)
    {
        using ArrayPoolList<byte> inputBuffer = new ArrayPoolList<byte>((int)length, (int)length);
        RandomAccess.Read(_file, inputBuffer.AsSpan(), offset);
        Stream inputStream = inputBuffer.AsMemory().AsStream();

        await using SnappyStream decompressor = new(inputStream, CompressionMode.Decompress, true);
        await using RecyclableMemoryStream stream = RecyclableStream.GetStream(nameof(E2StoreReader));
        await decompressor.CopyToAsync(stream, cancellation);

        if (!stream.TryGetBuffer(out ArraySegment<byte> segment))
        {
            throw new InvalidDataException("Unable to get buffer for memory stream");
        }

        return decoder(segment);
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

        // <offset> * 8 + <count>
        int indexLength = (int)_blockCount * IndexOffsetSize + IndexSectionCount;
        long offsetLocation = indexLength - (long)(blockNumber - _startBlock!) * IndexOffsetSize;

        // <header> + <start block> + <the rest of the index>
        int indexSizeIncludingHeader = HeaderSize + IndexSectionStartBlock + indexLength;

        // This is negative, relative to start of index (including header)
        long relativeOffset = ReadInt64(_fileLength - offsetLocation);
        return _fileLength - indexSizeIncludingHeader + relativeOffset;
    }

    private void EnsureIndexAvailable()
    {
        if (_startBlock != null) return;

        if (_fileLength < 32) throw new EraFormatException("Invalid era file. Too small to contain index.");

        // Read the block count
        _blockCount = (long)ReadUInt64(_fileLength - IndexSectionCount);

        // <starting block> + <offsets> * 8 + <count>
        int indexLength = IndexSectionStartBlock + (int)_blockCount * IndexOffsetSize + IndexSectionCount;

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

            // <index header> + <starting block> + <offset> * 8 + <count>
            int indexLengthIncludingHeader = HeaderSize + IndexSectionStartBlock + (int)_blockCount * IndexOffsetSize + IndexSectionCount;

            // <header> + <the 32 byte hash> + <indexes>
            int accumulatorFromLast = E2StoreWriter.HeaderSize + 32 + indexLengthIncludingHeader;

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
}
