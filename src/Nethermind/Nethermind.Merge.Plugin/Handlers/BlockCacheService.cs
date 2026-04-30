// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

public class BlockCacheService : IBlockCacheService
{
    private readonly int _maxCachedBlocks;

    public BlockCacheService()
        : this((int)(Reorganization.MaxDepth * 2 + 16))
    {
    }

    public BlockCacheService(int maxCachedBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedBlocks);
        _maxCachedBlocks = maxCachedBlocks;
    }

    public ConcurrentDictionary<Hash256AsKey, Block> BlockCache { get; } = new();
    public Hash256? FinalizedHash { get; set; }
    public Hash256? HeadBlockHash { get; set; }

    public bool TryAddBlock(Block block)
    {
        Hash256 blockHash = block.Hash ?? block.CalculateHash();
        bool added = BlockCache.TryAdd(blockHash, block);
        if (added)
        {
            PruneIfNeeded();
        }

        return added;
    }

    public void Clear() => BlockCache.Clear();

    private void PruneIfNeeded()
    {
        while (BlockCache.Count > _maxCachedBlocks)
        {
            if (!TryRemoveOldestBlock())
            {
                return;
            }
        }
    }

    private bool TryRemoveOldestBlock()
    {
        Hash256AsKey oldestHash = default;
        long oldestNumber = long.MaxValue;
        bool foundOldest = false;

        foreach (KeyValuePair<Hash256AsKey, Block> cachedBlock in BlockCache)
        {
            if (IsProtected(cachedBlock.Key))
            {
                continue;
            }

            if (!foundOldest || cachedBlock.Value.Number < oldestNumber)
            {
                oldestHash = cachedBlock.Key;
                oldestNumber = cachedBlock.Value.Number;
                foundOldest = true;
            }
        }

        return foundOldest && BlockCache.TryRemove(oldestHash, out _);
    }

    private bool IsProtected(Hash256AsKey blockHash) =>
        Equals(blockHash.Value, FinalizedHash) || Equals(blockHash.Value, HeadBlockHash);
}
