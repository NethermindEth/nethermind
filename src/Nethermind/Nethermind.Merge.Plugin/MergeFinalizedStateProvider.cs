// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Merge.Plugin;

public class MergeFinalizedStateProvider(IPoSSwitcher poSSwitcher, IBlockTree blockTree, IFinalizedStateProvider baseFinalizedStateProvider): IFinalizedStateProvider
{
    public long FinalizedBlockNumber
    {
        get
        {
            if (poSSwitcher.TransitionFinished)
            {
                return blockTree.FindHeader(BlockParameter.Finalized)?.Number ??
                       baseFinalizedStateProvider.FinalizedBlockNumber;
            };
            return poSSwitcher.TransitionFinished
                ? blockTree.FindHeader(BlockParameter.Finalized)?.Number ??
                  baseFinalizedStateProvider.FinalizedBlockNumber
                : baseFinalizedStateProvider.FinalizedBlockNumber;
        }
    }

    public Hash256? GetFinalizedStateRootAt(long blockNumber)
    {
        if (FinalizedBlockNumber < blockNumber) return null;
        return baseFinalizedStateProvider.GetFinalizedStateRootAt(blockNumber);
    }
}
