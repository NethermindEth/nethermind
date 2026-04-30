// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Stores payload blocks while their ancestry is resolved during merge sync.
/// </summary>
public class BlockCacheService : IBlockCacheService
{
    private readonly int _maxCachedBlocks;
    private readonly Lock _pruneLock = new();

    /// <summary>
    /// Initializes a block cache with the default merge sync bound.
    /// </summary>
    public BlockCacheService()
        : this((int)(Reorganization.MaxDepth * 2 + 16))
    {
    }

    /// <summary>
    /// Initializes a block cache with the provided maximum number of cached blocks.
    /// </summary>
    /// <param name="maxCachedBlocks">Maximum number of cached blocks before pruning unprotected entries.</param>
    public BlockCacheService(int maxCachedBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedBlocks);
        _maxCachedBlocks = maxCachedBlocks;
    }

    /// <inheritdoc />
    public ConcurrentDictionary<Hash256AsKey, Block> BlockCache { get; } = new();

    /// <inheritdoc />
    public Hash256? FinalizedHash { get; set; }

    /// <inheritdoc />
    public Hash256? HeadBlockHash { get; set; }

    /// <inheritdoc />
    public bool TryAddBlock(Block block)
    {
        Hash256 blockHash = block.Hash ?? block.CalculateHash();
        bool added = BlockCache.TryAdd(blockHash, block);
        if (added)
        {
            lock (_pruneLock)
            {
                PruneIfNeeded();
            }
        }

        return added;
    }

    /// <inheritdoc />
    public void Clear() => BlockCache.Clear();

    private void PruneIfNeeded()
    {
        while (BlockCache.Count > _maxCachedBlocks)
        {
            if (!TryRemoveFurthestBlock())
            {
                return;
            }
        }
    }

    private bool TryRemoveFurthestBlock()
    {
        Hash256AsKey furthestHash = default;
        long furthestNumber = long.MinValue;
        bool foundFurthest = false;

        foreach (KeyValuePair<Hash256AsKey, Block> cachedBlock in BlockCache)
        {
            if (IsProtected(cachedBlock.Key))
            {
                continue;
            }

            if (!foundFurthest || cachedBlock.Value.Number > furthestNumber)
            {
                furthestHash = cachedBlock.Key;
                furthestNumber = cachedBlock.Value.Number;
                foundFurthest = true;
            }
        }

        return foundFurthest && BlockCache.TryRemove(furthestHash, out _);
    }

    private bool IsProtected(Hash256AsKey blockHash) =>
        Equals(blockHash.Value, FinalizedHash) || Equals(blockHash.Value, HeadBlockHash);
}
