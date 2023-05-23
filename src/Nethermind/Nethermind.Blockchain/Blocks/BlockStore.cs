// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

public class BlockStore : IBlockStore
{
    private readonly IDb _blockDb;
    private readonly IDbWithSpan _blockDbAsSpan;
    private readonly BlockDecoder _blockDecoder = new();
    private const int CacheSize = 64;

    private readonly LruCache<KeccakKey, Block>
        _blockCache = new(CacheSize, CacheSize, "blocks");

    public BlockStore(IDb blockDb)
    {
        _blockDb = blockDb;
        _blockDbAsSpan = (IDbWithSpan)_blockDb;
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
        _blockDb.Set(block.Hash, newRlp.AsSpan());
    }

    public void Delete(Keccak blockHash)
    {
        _blockDb.Delete(blockHash);
        _blockCache.Delete(blockHash);
    }

    public Block? Get(Keccak blockHash, bool shouldCache)
    {

        return _blockDb.Get(blockHash, _blockDecoder, _blockCache, shouldCache);
    }

    public ReceiptRecoveryBlock GetReceiptRecoveryBlock(Keccak blockHash)
    {
        MemoryManager<byte> memory = _blockDbAsSpan.GetOwnedMemory(blockHash.Bytes);

        return _blockDecoder.DecodeToReceiptRecoveryBlock(memory, RlpBehaviors.None);
    }

    public void Cache(Block block)
    {
        _blockCache.Set(block.Hash, block);
    }
}
