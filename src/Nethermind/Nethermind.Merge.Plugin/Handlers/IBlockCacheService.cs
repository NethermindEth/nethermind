// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IBlockCacheService
{
    /// <summary>
    /// Blocks cached while payload ancestry is resolved during merge sync.
    /// </summary>
    public IReadOnlyDictionary<Hash256AsKey, Block> BlockCache { get; }

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
    /// <returns><see langword="true"/> if the block remains cached; otherwise <see langword="false"/>.</returns>
    bool TryAddBlock(Block block);

    /// <summary>
    /// Removes a block from the cache.
    /// </summary>
    /// <param name="blockHash">Hash of the block to remove.</param>
    /// <returns><see langword="true"/> if the block was removed; otherwise <see langword="false"/>.</returns>
    bool TryRemoveBlock(Hash256 blockHash);

    /// <summary>
    /// Removes all cached blocks.
    /// </summary>
    void Clear();
}
