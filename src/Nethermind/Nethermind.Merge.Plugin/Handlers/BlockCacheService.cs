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
    private readonly ConcurrentDictionary<Hash256AsKey, Block> _blockCache = new();
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
    public IReadOnlyDictionary<Hash256AsKey, Block> BlockCache => _blockCache;

    /// <inheritdoc />
    public Hash256? FinalizedHash { get; set; }

    /// <inheritdoc />
    public Hash256? HeadBlockHash { get; set; }

    /// <inheritdoc />
    public bool TryAddBlock(Block block)
    {
        Hash256 blockHash = block.Hash ?? block.CalculateHash();
        bool added = _blockCache.TryAdd(blockHash, block);
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
    public bool TryRemoveBlock(Hash256 blockHash)
    {
        lock (_pruneLock)
        {
            return _blockCache.TryRemove(blockHash, out _);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_pruneLock)
        {
            _blockCache.Clear();
        }
    }

    private void PruneIfNeeded()
    {
        while (_blockCache.Count > _maxCachedBlocks)
        {
            if (!TryRemoveHighestNumberedBlock())
            {
                break;
            }
        }
    }

    private bool TryRemoveHighestNumberedBlock()
    {
        Hash256AsKey furthestHash = default;
        long furthestNumber = long.MinValue;
        bool foundFurthest = false;

        foreach (KeyValuePair<Hash256AsKey, Block> cachedBlock in _blockCache)
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

        return foundFurthest && _blockCache.TryRemove(furthestHash, out _);
    }

    private bool IsProtected(Hash256AsKey blockHash) =>
        Equals(blockHash.Value, FinalizedHash) || Equals(blockHash.Value, HeadBlockHash);
}
