// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Microsoft.IO;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Snappier;

namespace Nethermind.Blockchain.Blocks;

public class BlockStore : IBlockStore
{
    private string? _basePath;
    private readonly IDb _blockDb;
    private readonly BlockDecoder _blockDecoder = new();
    private const int CacheSize = 128 + 32;
    private const int BlocksPerEra = 8192;
    private const int FileSplit = 8;
    private const int BlocksPerFile = BlocksPerEra / FileSplit;

    private readonly ConcurrentDictionary<int, McsLock> _locks = new();
    private readonly LruCache<ValueHash256, Block>
        _blockCache = new(CacheSize, CacheSize, "blocks");
    private readonly long? _maxSize;

    public BlockStore(IDb blockDb, long? maxSize = null)
    {
        _blockDb = blockDb;
        _maxSize = maxSize;
        var basePath = blockDb.DbPath;
        if (basePath is not null)
        {
            _basePath = Path.Combine(basePath, "blockfiles");
        }
    }

    public void SetMetadata(byte[] key, byte[] value)
    {
        _blockDb.Set(key, value);
    }

    public byte[]? GetMetadata(byte[] key)
    {
        return _blockDb.Get(key);
    }

    private void TruncateToMaxSize()
    {
        int toDelete = (int)(_blockDb.GatherMetric().Size - _maxSize!);
        if (toDelete > 0)
        {
            foreach (var blockToDelete in GetAll().Take(toDelete))
            {
                Delete(blockToDelete.Number, blockToDelete.Hash);
            }
        }
    }

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None, bool shouldCache = false)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        if (shouldCache) Cache(block);

        if (_basePath is not null)
        {
            SaveToFile(block);
        }
        else
        {
            SaveToDb(block, writeFlags);
        }
    }

    private void SaveToDb(Block block, WriteFlags writeFlags)
    {
        // if we carry Rlp from the network message all the way here we could avoid encoding back to RLP here
        // Although cpu is the main bottleneck since NettyRlpStream uses pooled memory which avoid unnecessary allocations..
        using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);

        _blockDb.Set(block.Number, block.Hash, newRlp.AsSpan(), writeFlags);

        if (_maxSize is not null)
        {
            TruncateToMaxSize();
        }
    }

    private void SaveToFile(Block block)
    {
        (string directory, string filename, int lockId) = GetBlockDirectoryAndPath(block.Number, _basePath);
        RecyclableMemoryStream output = RecyclableStream.GetStream(filename);
        using (RecyclableRlpStream newRlp = _blockDecoder.EncodeToNewRecyclableRlpStream(block, filename))
        {
            using SnappyStream compressor = new(output, CompressionMode.Compress, leaveOpen: true);
            newRlp.CopyTo(compressor);
        }

        output.Position = 0;

        string path = Path.Combine(directory, filename);

        bool newLock = false;
        var handle = _locks.GetOrAdd(lockId, _ =>
        {
            newLock = true;
            return new McsLock();
        }).Acquire();
        if (newLock)
        {
            Directory.CreateDirectory(directory);
        }

        FileStream file = new(path, mode: FileMode.OpenOrCreate, access: FileAccess.Write, share: FileShare.Read);
        SafeFileHandle fileHandle = file.SafeFileHandle;
        long fileOffset = Math.Max(RandomAccess.GetLength(fileHandle), Vector128<byte>.Count * BlocksPerFile);

        long outputLength = output.Length;
        if (output.TryGetBuffer(out ArraySegment<byte> buffer))
        {
            ReadOnlySpan<byte> outputData = buffer.Array.AsSpan(buffer.Offset, (int)outputLength);
            RandomAccess.Write(fileHandle, outputData, fileOffset);
        }
        else
        {
            WriteReadOnlySequence(fileHandle, output, fileOffset);
        }

        long headerOffset = (block.Number / FileSplit) % BlocksPerFile;
        Vector128<long> headerEntry = Vector128.Create(fileOffset, outputLength);
        ReadOnlySpan<byte> headerData = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref headerEntry, 1));
        RandomAccess.Write(fileHandle, headerData, Vector128<byte>.Count * headerOffset);

        static void WriteReadOnlySequence(SafeFileHandle fileHandle, RecyclableMemoryStream output, long fileOffset)
        {
            ReadOnlySequence<byte> sequence = output.GetReadOnlySequence();
            foreach (ReadOnlyMemory<byte> memory in sequence)
            {
                ReadOnlySpan<byte> outputData = memory.Span;
                RandomAccess.Write(fileHandle, outputData, fileOffset);
                fileOffset += outputData.Length;
            }
        }
    }

    private (string directory, string filename, int lockId) GetBlockDirectoryAndPath(long blockNumber, string basePath)
    {
        var era = (int)(blockNumber / BlocksPerEra);
        long eraGroup = era / 100;
        int blockDigit = (int)(blockNumber % FileSplit);

        string directory = Path.Combine(basePath, eraGroup.ToString("D3"));
        string path = $"{era:D5}-{blockDigit:D1}.sz";

        return (directory, path, era * FileSplit + blockDigit);
    }

    private static void GetBlockNumPrefixedKey(long blockNumber, Hash256 blockHash, Span<byte> output)
    {
        blockNumber.WriteBigEndian(output);
        blockHash!.Bytes.CopyTo(output[8..]);
    }

    public void Delete(long blockNumber, Hash256 blockHash)
    {
        _blockCache.Delete(blockHash);
        _blockDb.Delete(blockNumber, blockHash);
        _blockDb.Remove(blockHash.Bytes);
    }

    public Block? Get(long blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = false)
    {
        if (_blockCache.TryGet(blockHash, out Block? cachedBlock))
        {
            return cachedBlock;
        }

        string? path = null;
        string? filename = null;
        if (_basePath is not null)
        {
            (string directory, filename, _) = GetBlockDirectoryAndPath(blockNumber, _basePath);
            path = Path.Combine(directory, filename);
        }
        if (path is not null && File.Exists(path))
        {
            using FileStream file = new(path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite);
            var fileHandle = file.SafeFileHandle;
            long headerOffset = (blockNumber / FileSplit) % BlocksPerFile;

            Vector128<long> headerEntry = default;
            Span<byte> headerData = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref headerEntry, 1));
            long read = RandomAccess.Read(fileHandle, headerData, Vector128<byte>.Count * headerOffset);
            if (read == Vector128<byte>.Count && headerEntry != default)
            {
                long offset = headerEntry.GetElement(0);
                int length = (int)headerEntry.GetElement(1);

                var array = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    var input = array.AsSpan(0, length);
                    read = RandomAccess.Read(fileHandle, input, offset);
                    if (read == length)
                    {
                        using RecyclableMemoryStream outputStream = RecyclableStream.GetStream(filename);
                        var compressed = new MemoryStream(array, 0, length, writable: false);
                        using (SnappyStream decompressor = new(compressed, CompressionMode.Decompress))
                        {
                            decompressor.CopyTo(outputStream);
                        }

                        ArrayPool<byte>.Shared.Return(array);
                        array = null;

                        outputStream.Position = 0;
                        var rlp = new RecyclableRlpStream(outputStream);
                        var block = _blockDecoder.Decode(rlp, rlpBehaviors | RlpBehaviors.AllowExtraBytes);
                        if (shouldCache)
                        {
                            Cache(block);
                        }

                        return block;
                    }
                }
                finally
                {
                    if (array is not null) ArrayPool<byte>.Shared.Return(array);
                }
            }
        }

        Block? b = _blockDb.Get(blockNumber, blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
        if (b is not null) return b;
        return _blockDb.Get(blockHash, _blockDecoder, _blockCache, rlpBehaviors, shouldCache);
    }

    public byte[]? GetRaw(long blockNumber, Hash256 blockHash)
    {
        string? path = null;
        string? filename = null;
        if (_basePath is not null)
        {
            (string directory, filename, _) = GetBlockDirectoryAndPath(blockNumber, _basePath);
            path = Path.Combine(directory, filename);
        }
        if (path is not null && File.Exists(path))
        {
            using FileStream file = new(path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite);
            var fileHandle = file.SafeFileHandle;
            long headerOffset = (blockNumber / FileSplit) % BlocksPerFile;

            Vector128<long> headerEntry = default;
            Span<byte> headerData = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref headerEntry, 1));
            long read = RandomAccess.Read(fileHandle, headerData, Vector128<byte>.Count * headerOffset);
            if (read == Vector128<byte>.Count && headerEntry != default)
            {
                long offset = headerEntry.GetElement(0);
                int length = (int)headerEntry.GetElement(1);

                var array = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    var input = array.AsSpan(0, length);
                    read = RandomAccess.Read(fileHandle, input, offset);
                    if (read == length)
                    {
                        using RecyclableMemoryStream outputStream = RecyclableStream.GetStream(filename);
                        var compressed = new MemoryStream(array, 0, length, writable: false);
                        using (SnappyStream decompressor = new(compressed, CompressionMode.Decompress))
                        {
                            decompressor.CopyTo(outputStream);
                        }

                        ArrayPool<byte>.Shared.Return(array);
                        array = null;

                        outputStream.Position = 0;
                        return outputStream.ToArray();
                    }
                }
                finally
                {
                    if (array is not null) ArrayPool<byte>.Shared.Return(array);
                }

            }
        }

        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        var b = _blockDb.Get(dbKey);
        if (b is not null) return b;
        return _blockDb.Get(blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Hash256 blockHash)
    {
        var bytes = GetRaw(blockNumber, blockHash);
        if (bytes is not null)
        {
            return BlockDecoder.DecodeToReceiptRecoveryBlock(null, new Memory<byte>(bytes), RlpBehaviors.None);
        }

        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        MemoryManager<byte>? memoryOwner = _blockDb.GetOwnedMemory(keyWithBlockNumber);
        memoryOwner ??= _blockDb.GetOwnedMemory(blockHash.Bytes);

        return BlockDecoder.DecodeToReceiptRecoveryBlock(memoryOwner, memoryOwner?.Memory ?? Memory<byte>.Empty, RlpBehaviors.None);
    }

    public void Cache(Block block)
    {
        _blockCache.Set(block.Hash, block);
    }

    public IEnumerable<Block> GetAll()
    {
        return _blockDb.GetAllValues(true).Select(bytes => _blockDecoder.Decode(bytes.AsRlpStream()));
    }

}
