// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

public class BlockStore : IBlockStore
{
    private readonly IDb _blockDb;
    private readonly BlockDecoder _blockDecoder = new();
    private const int CacheSize = 128 + 32;

    private readonly LruCache<ValueHash256, Block>
        _blockCache = new(CacheSize, CacheSize, "blocks");
    private long? _maxSize;

    public BlockStore(IDb blockDb, long? maxSize = null)
    {
        _blockDb = blockDb;
        _maxSize = maxSize;
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
        int toDelete = (int)(_blockDb.GetSize() - _maxSize!);
        if (toDelete > 0)
        {
            foreach (var blockToDelete in GetAll().Take(toDelete))
            {
                Delete(blockToDelete.Number, blockToDelete.Hash);
            }
        }
    }

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        // if we carry Rlp from the network message all the way here then we could solve 4GB of allocations and some processing
        // by avoiding encoding back to RLP here (allocations measured on a sample 3M blocks Goerli fast sync
        using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);

        _blockDb.Set(block.Number, block.Hash, newRlp.AsSpan(), writeFlags);

        if (_maxSize is not null)
        {
            TruncateToMaxSize();
        }
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

    public Block? Get(long blockNumber, Hash256 blockHash, bool shouldCache = false)
    {
        Block? b = _blockDb.Get(blockNumber, blockHash, _blockDecoder, _blockCache, shouldCache);
        if (b != null) return b;
        return _blockDb.Get(blockHash, _blockDecoder, _blockCache, shouldCache);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Hash256 blockHash)
    {
        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        MemoryManager<byte>? memoryOwner = _blockDb.GetOwnedMemory(keyWithBlockNumber);
        if (memoryOwner == null)
        {
            memoryOwner = _blockDb.GetOwnedMemory(blockHash.Bytes);
        }

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
