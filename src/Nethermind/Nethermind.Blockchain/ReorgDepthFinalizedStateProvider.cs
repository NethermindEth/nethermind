// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain;

public class ReorgDepthFinalizedStateProvider(IBlockTree blockTree) : IFinalizedStateProvider
{
    public long FinalizedBlockNumber
    {
        get
        {
            ulong maxDepth = (ulong)Reorganization.MaxDepth;
            ulong finalized = blockTree.BestKnownNumber > maxDepth ? blockTree.BestKnownNumber - maxDepth : 0UL;
            return checked((long)finalized);
        }
    }

    public Hash256? GetFinalizedStateRootAt(long blockNumber)
    {
        if (blockNumber < 0) return null;
        if (FinalizedBlockNumber < blockNumber) return null;
        return blockTree.FindHeader(checked((ulong)blockNumber), BlockTreeLookupOptions.RequireCanonical)?.StateRoot;
    }
}
