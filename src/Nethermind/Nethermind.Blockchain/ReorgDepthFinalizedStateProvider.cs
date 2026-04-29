// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;
using System;

namespace Nethermind.Blockchain;

public class ReorgDepthFinalizedStateProvider(IBlockTree blockTree) : IFinalizedStateProvider
{
    public long FinalizedBlockNumber
    {
        get
        {
            Hash256? finalizedHash = blockTree.FinalizedHash;
            if (finalizedHash is not null && finalizedHash != Keccak.Zero)
            {
                BlockHeader? finalizedHeader = blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (finalizedHeader is not null) return finalizedHeader.Number;
            }
            return Math.Max(0, blockTree.BestKnownNumber - Reorganization.MaxDepth);
        }
    }

    public Hash256? GetFinalizedStateRootAt(long blockNumber)
    {
        if (FinalizedBlockNumber < blockNumber) return null;
        return blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical)?.StateRoot;
    }
}
