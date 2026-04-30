// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IBlockCacheService
{
    /// <summary>
    /// Blocks cached while payload ancestry is resolved during merge sync.
    /// </summary>
    /// <remarks>
    /// Additions must go through <see cref="TryAddBlock"/> so the cache size bound is enforced.
    /// </remarks>
    public ConcurrentDictionary<Hash256AsKey, Block> BlockCache { get; }

    /// <summary>
    /// Finalized block hash protected from cache pruning.
    /// </summary>
    Hash256? FinalizedHash { get; set; }

    /// <summary>
    /// Head block hash protected from cache pruning.
    /// </summary>
    Hash256? HeadBlockHash { get; set; }

    /// <summary>
    /// Adds a block to the bounded cache.
    /// </summary>
    /// <param name="block">Block to add.</param>
    /// <returns><see langword="true"/> if the block was added; otherwise <see langword="false"/>.</returns>
    bool TryAddBlock(Block block);

    /// <summary>
    /// Removes all cached blocks.
    /// </summary>
    void Clear();
}
