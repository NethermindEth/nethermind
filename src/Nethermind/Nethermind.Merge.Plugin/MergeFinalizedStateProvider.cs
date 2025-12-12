// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Trie.Pruning;

namespace Nethermind.Merge.Plugin;

public class MergeFinalizedStateProvider(IPoSSwitcher poSSwitcher, IBlockCacheService blockCacheService, IBlockTree blockTree, IFinalizedStateProvider baseFinalizedStateProvider) : IFinalizedStateProvider
{
    public long FinalizedBlockNumber
    {
        get
        {
            if (poSSwitcher.TransitionFinished)
            {
                BlockHeader? currentFinalized = null;
                if (blockTree.FinalizedHash is { } blockTreeFinalizedHash)
                {
                    currentFinalized = blockTree.FindHeader(blockTreeFinalizedHash, BlockTreeLookupOptions.None);
                }

                // Finalized hash from blocktree is not updated until it is processed, which is a problem for long
                // catchup. So we use from blockCacheService as a backup.
                if (blockCacheService.FinalizedHash is { } blockCacheFinalizedHash)
                {
                    BlockHeader? fromBlockCache = blockTree.FindHeader(blockCacheFinalizedHash);
                    if (fromBlockCache is not null)
                    {
                        if (currentFinalized is null || fromBlockCache.Number > currentFinalized.Number)
                        {
                            currentFinalized = fromBlockCache;
                        }
                    }
                }

                return currentFinalized?.Number ?? baseFinalizedStateProvider.FinalizedBlockNumber;
            }

            return baseFinalizedStateProvider.FinalizedBlockNumber;
        }
    }

    public Hash256? GetFinalizedStateRootAt(long blockNumber)
    {
        if (FinalizedBlockNumber < blockNumber) return null;
        return baseFinalizedStateProvider.GetFinalizedStateRootAt(blockNumber);
    }
}
