// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Stores payload blocks while their ancestry is resolved during merge sync.
/// </summary>
public class BlockCacheService : IBlockCacheService
{
    private readonly int _maxCachedBlocks;
    private readonly IBeaconSyncStrategy _beaconSyncStrategy;
    private readonly ConcurrentDictionary<Hash256AsKey, Block> _blockCache = new();

    /// <summary>
    /// Initializes a block cache with the default merge sync bound.
    /// </summary>
    /// <param name="beaconSyncStrategy">Source of the finalized/head hashes protected from pruning. Defaults to <see cref="No.BeaconSync"/>.</param>
    public BlockCacheService(IBeaconSyncStrategy? beaconSyncStrategy = null)
        : this((int)(Reorganization.MaxDepth * 2 + 16), beaconSyncStrategy)
    {
    }

    /// <summary>
    /// Initializes a block cache with the provided maximum number of cached blocks.
    /// </summary>
    /// <param name="maxCachedBlocks">Maximum number of cached blocks before pruning unprotected entries.</param>
    /// <param name="beaconSyncStrategy">Source of the finalized/head hashes protected from pruning. Defaults to <see cref="No.BeaconSync"/>.</param>
    public BlockCacheService(int maxCachedBlocks, IBeaconSyncStrategy? beaconSyncStrategy = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedBlocks);
        _maxCachedBlocks = maxCachedBlocks;
        _beaconSyncStrategy = beaconSyncStrategy ?? No.BeaconSync;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Hash256AsKey, Block> BlockCache => _blockCache;

    /// <inheritdoc />
    public bool TryAddBlock(Block block)
    {
        Hash256 blockHash = block.GetOrCalculateHash();
        if (!_blockCache.TryAdd(blockHash, block))
        {
            return false;
        }

        bool blockRemainsCached = true;
        // The cache bound is small, so pruning scans it instead of maintaining a second ordered index.
        while (_blockCache.Count > _maxCachedBlocks &&
               TryGetHighestNumberedUnprotectedBlock(out Hash256AsKey blockHashToRemove))
        {
            bool removed = _blockCache.TryRemove(blockHashToRemove, out _);
            if (removed && Equals(blockHashToRemove.Value, blockHash))
            {
                blockRemainsCached = false;
            }
        }

        return blockRemainsCached;
    }

    /// <inheritdoc />
    public bool TryRemoveBlock(Hash256 blockHash) => _blockCache.TryRemove(blockHash, out _);

    /// <inheritdoc />
    public void Clear() => _blockCache.Clear();

    private bool TryGetHighestNumberedUnprotectedBlock(out Hash256AsKey blockHash)
    {
        Hash256AsKey highestNumberedHash = default;
        long highestBlockNumber = long.MinValue;
        bool foundBlock = false;

        foreach (KeyValuePair<Hash256AsKey, Block> cachedBlock in _blockCache)
        {
            if (IsProtected(cachedBlock.Key))
            {
                continue;
            }

            if (!foundBlock || cachedBlock.Value.Number > highestBlockNumber)
            {
                highestNumberedHash = cachedBlock.Key;
                highestBlockNumber = cachedBlock.Value.Number;
                foundBlock = true;
            }
        }

        blockHash = highestNumberedHash;
        return foundBlock;
    }

    private bool IsProtected(Hash256AsKey blockHash) =>
        Equals(blockHash.Value, _beaconSyncStrategy.GetFinalizedHash()) || Equals(blockHash.Value, _beaconSyncStrategy.GetHeadBlockHash());
}
