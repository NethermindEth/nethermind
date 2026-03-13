// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using CommunityToolkit.HighPerformance;
using Microsoft.IO;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.EraE.Exceptions;
using Snappier;
using EraException = Nethermind.Era1.EraException;
using Entry = Nethermind.Era1.Entry;
using EraWriter = Nethermind.EraE.Archive.EraWriter;

namespace Nethermind.EraE.E2Store;

/// <summary>
/// Random-access reader for EraE files. Parses the ComponentIndex to locate
/// headers, bodies, slim-receipts, and (optionally) total-difficulty entries.
/// </summary>
public sealed class E2StoreReader : IDisposable
{
    private const int EntryHeaderSize = 8;
    private const int ValueSizeLimit = 1024 * 1024 * 50;

    private readonly SafeFileHandle _file;
    private readonly long _fileLength;

    private long _startBlock;
    private long _blockCount;
    private int _componentCount; // 3 (post-merge) or 4 (pre-merge/transition)
    private bool _indexLoaded;
    private long _componentIndexTlvStart; // absolute position of ComponentIndex entry header

    public E2StoreReader(string filePath)
    {
        _file = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _fileLength = RandomAccess.GetLength(_file);
    }

    public long First
    {
        get
        {
            EnsureIndexLoaded();
            return _startBlock;
        }
    }

    public long LastBlock => First + _blockCount - 1;

    public long BlockCount
    {
        get
        {
            EnsureIndexLoaded();
            return _blockCount;
        }
    }

    public bool HasTotalDifficulty
    {
        get
        {
            EnsureIndexLoaded();
            return _componentCount >= 4;
        }
    }

    /// <summary>Returns the absolute file offset of the CompressedHeader entry for the given block.</summary>
    public long HeaderOffset(long blockNumber) => ComponentOffset(blockNumber, 0);

    /// <summary>Returns the absolute file offset of the CompressedBody entry for the given block.</summary>
    public long BodyOffset(long blockNumber) => ComponentOffset(blockNumber, 1);

    /// <summary>Returns the absolute file offset of the CompressedSlimReceipts entry for the given block.</summary>
    public long SlimReceiptsOffset(long blockNumber) => ComponentOffset(blockNumber, 2);

    /// <summary>
    /// Returns the absolute file offset of the TotalDifficulty entry for the given block,
    /// or throws if this is a post-merge-only epoch.
    /// </summary>
    public long TotalDifficultyOffset(long blockNumber)
    {
        EnsureIndexLoaded();
        if (_componentCount < 4)
            throw new EraException("This EraE file does not contain TotalDifficulty entries (post-merge epoch).");
        return ComponentOffset(blockNumber, 3);
    }

    /// <summary>Returns the absolute file offset of the AccumulatorRoot entry, or -1 if not present.</summary>
    public long AccumulatorRootOffset
    {
        get
        {
            EnsureIndexLoaded();
            if (_componentCount < 4)
                return -1;

            // AccumulatorRoot is the entry immediately before ComponentIndex.
            // ComponentIndex entry header is at _componentIndexTlvStart.
            // Before it: AccumulatorRoot entry = [8-byte header][32-byte hash]
            return _componentIndexTlvStart - EntryHeaderSize - 32;
        }
    }

    public long ReadEntryAndDecode<T>(long position, Func<Memory<byte>, T> decoder, ushort expectedType, out T value)
    {
        Entry entry = ReadEntry(position, expectedType);
        int length = (int)entry.Length;
        using ArrayPoolListRef<byte> buffer = new(length, length);
        RandomAccess.Read(_file, buffer.AsSpan(), position + EntryHeaderSize);
        value = decoder(buffer.AsMemory());
        return (long)entry.Length + EntryHeaderSize;
    }

    public async Task<(T, long)> ReadSnappyCompressedEntryAndDecode<T>(
        long position, Func<Memory<byte>, T> decoder, ushort expectedType, CancellationToken token = default)
    {
        Entry entry = ReadEntry(position, expectedType);
        T value = await ReadEntryValueAsSnappy(position + EntryHeaderSize, entry.Length, decoder, token);
        return (value, (long)entry.Length + EntryHeaderSize);
    }

    public Entry ReadEntry(long position, ushort? expectedType, CancellationToken token = default)
    {
        ushort type = ReadUInt16(position);
        uint length = ReadUInt32(position + 2);
        ushort reserved = ReadUInt16(position + 6);

        Entry entry = new(type, length);
        if (expectedType.HasValue && entry.Type != expectedType)
            throw new EraException($"Expected entry type 0x{expectedType:X4} but got 0x{entry.Type:X4} at position {position}.");
        if (entry.Length + (ulong)position > (ulong)_fileLength)
            throw new EraFormatException($"Entry length {entry.Length} at position {position} exceeds file length {_fileLength}.");
        if (entry.Length > ValueSizeLimit)
            throw new EraException($"Entry size {entry.Length} exceeds limit {ValueSizeLimit}.");
        if (reserved != 0)
            throw new EraFormatException($"Reserved header bytes are non-zero at position {position}.");
        return entry;
    }

    public ValueHash256 CalculateChecksum()
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int bufferSize = 81920;
        byte[] buffer = new byte[bufferSize];
        long offset = 0;
        while (offset < _fileLength)
        {
            int toRead = (int)Math.Min(bufferSize, _fileLength - offset);
            int read = RandomAccess.Read(_file, buffer.AsSpan(0, toRead), offset);
            if (read == 0) break;
            sha.AppendData(buffer, 0, read);
            offset += read;
        }
        return new ValueHash256(sha.GetHashAndReset());
    }

    public void Dispose() => _file.Dispose();

    private void EnsureIndexLoaded()
    {
        if (_indexLoaded) return;

        // ComponentIndex is the last entry in the file.
        // Layout of ComponentIndex data:
        //   starting_number  (8 bytes)
        //   [comp offsets]   (blockCount * componentCount * 8 bytes)
        //   component_count  (8 bytes)
        //   block_count      (8 bytes)

        // Minimum viable file: entry header (8) + starting_number (8) + component_count (8) + block_count (8)
        if (_fileLength < EntryHeaderSize + 24)
            throw new EraFormatException($"File too small ({_fileLength} bytes) to contain a valid ComponentIndex.");

        // Read block_count from the last 8 bytes of file
        _blockCount = ReadInt64(_fileLength - 8);
        if (_blockCount <= 0 || _blockCount > EraWriter.MaxEraSize)
            throw new EraFormatException($"Invalid block count {_blockCount} in EraE ComponentIndex.");

        // Read component_count from the 8 bytes before block_count
        _componentCount = (int)ReadInt64(_fileLength - 16);
        // 3 = post-merge (header, body, receipts)
        // 4 = pre-merge or transition (+ total-difficulty), or post-merge with proof
        // 5 = transition with both total-difficulty and proof (future)
        if (_componentCount < 3 || _componentCount > 5)
            throw new EraFormatException($"Invalid component count {_componentCount} in EraE ComponentIndex.");

        // Total data length = 8 + blockCount * componentCount * 8 + 8 + 8
        long indexDataLength = 8 + _blockCount * _componentCount * 8 + 8 + 8;

        // Verify entry type
        long indexEntryStart = _fileLength - EntryHeaderSize - indexDataLength;
        _ = ReadEntry(indexEntryStart, EntryTypes.ComponentIndex);

        _componentIndexTlvStart = indexEntryStart;

        // Read starting block number (first 8 bytes of index data)
        _startBlock = ReadInt64(indexEntryStart + EntryHeaderSize);

        _indexLoaded = true;
    }

    private long ComponentOffset(long blockNumber, int componentIdx)
    {
        EnsureIndexLoaded();

        if (blockNumber < _startBlock || blockNumber >= _startBlock + _blockCount)
            throw new ArgumentOutOfRangeException(nameof(blockNumber), $"Block {blockNumber} is outside range [{_startBlock}, {_startBlock + _blockCount - 1}].");
        if (componentIdx >= _componentCount)
            throw new ArgumentOutOfRangeException(nameof(componentIdx));

        long blockIdx = blockNumber - _startBlock;
        // Offset table starts at: indexEntryStart + EntryHeaderSize + 8 (starting_number)
        long offsetFieldPos = _componentIndexTlvStart + EntryHeaderSize + 8
            + blockIdx * _componentCount * 8
            + componentIdx * 8;

        long relativeOffset = ReadInt64(offsetFieldPos);
        // Offset is relative to the ComponentIndex TLV start (entry header included)
        return _componentIndexTlvStart + relativeOffset;
    }

    private async Task<T> ReadEntryValueAsSnappy<T>(long offset, ulong length, Func<Memory<byte>, T> decoder, CancellationToken cancellation = default)
    {
        using ArrayPoolList<byte> inputBuffer = new((int)length, (int)length);
        RandomAccess.Read(_file, inputBuffer.AsSpan(), offset);
        Stream inputStream = inputBuffer.AsMemory().AsStream();

        await using SnappyStream decompressor = new(inputStream, CompressionMode.Decompress, true);
        await using RecyclableMemoryStream stream = RecyclableStream.GetStream(nameof(E2StoreReader));
        await decompressor.CopyToAsync(stream, cancellation);

        if (!stream.TryGetBuffer(out ArraySegment<byte> segment))
            throw new InvalidDataException("Unable to get buffer from memory stream.");

        return decoder(segment);
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
}
