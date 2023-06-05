// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    private readonly IDbWithSpan? _blockDbAsSpan;
    private readonly BlockDecoder _blockDecoder = new();
    private const int CacheSize = 128 + 32;

    private readonly LruCache<ValueKeccak, Block>
        _blockCache = new(CacheSize, CacheSize, "blocks");

    public BlockStore(IDb blockDb)
    {
        _blockDb = blockDb;

        if (blockDb is IDbWithSpan blockDbAsSpan)
            _blockDbAsSpan = blockDbAsSpan;
        else
            _blockDbAsSpan = null;
    }

    public void SetMetadata(byte[] key, byte[] value)
    {
        _blockDb.Set(key, value);
    }

    public byte[]? GetMetadata(byte[] key)
    {
        return _blockDb.Get(key);
    }

    public void Insert(Block block)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        // if we carry Rlp from the network message all the way here then we could solve 4GB of allocations and some processing
        // by avoiding encoding back to RLP here (allocations measured on a sample 3M blocks Goerli fast sync
        using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);

        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        GetBlockNumPrefixedKey(block.Number, block.Hash, keyWithBlockNumber);

        _blockDb.Set(keyWithBlockNumber, newRlp.AsSpan());
    }

    private static void GetBlockNumPrefixedKey(long blockNumber, Keccak blockHash, Span<byte> output)
    {
        blockNumber.WriteBigEndian(output);
        blockHash!.Bytes.CopyTo(output[8..]);
    }

    public void Delete(long blockNumber, Keccak blockHash)
    {
        _blockDb.Remove(blockHash.Bytes);
        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);
        _blockDb.Remove(keyWithBlockNumber);
        _blockCache.Delete(blockHash);
    }

    public Block? Get(long blockNumber, Keccak blockHash, bool shouldCache = false)
    {
        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        Block? b = _blockDb.Get(blockHash, keyWithBlockNumber, _blockDecoder, _blockCache, shouldCache);
        if (b != null) return b;
        return _blockDb.Get(blockHash, _blockDecoder, _blockCache, shouldCache);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Keccak blockHash)
    {
        Span<byte> keyWithBlockNumber = stackalloc byte[40];
        GetBlockNumPrefixedKey(blockNumber, blockHash, keyWithBlockNumber);

        MemoryManager<byte>? memoryOwner = null;
        Memory<byte> memory;
        if (_blockDbAsSpan != null)
        {

            memoryOwner = _blockDbAsSpan.GetOwnedMemory(keyWithBlockNumber);
            if (memoryOwner == null) {
                memoryOwner = _blockDbAsSpan.GetOwnedMemory(blockHash.Bytes);
            }

            memory = memoryOwner.Memory;

        }
        else
        {
            byte[]? data = _blockDb.Get(keyWithBlockNumber);
            if (data == null) {
                data = _blockDb.Get(blockHash.Bytes);
            }

            memory = data;
        }

        return _blockDecoder.DecodeToReceiptRecoveryBlock(memoryOwner, memory, RlpBehaviors.None);
    }

    public void Cache(Block block)
    {
        _blockCache.Set(block.Hash, block);
    }
}
