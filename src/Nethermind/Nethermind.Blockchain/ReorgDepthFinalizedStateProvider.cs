// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;
using System;

namespace Nethermind.Blockchain;

public class ReorgDepthFinalizedStateProvider(IBlockTree blockTree) : IFinalizedStateProvider
{
    private sealed record FinalizedCache(Hash256 Hash, long Number);
    private volatile FinalizedCache? _cache;

    public long FinalizedBlockNumber
    {
        get
        {
            Hash256? finalizedHash = blockTree.FinalizedHash;
            if (finalizedHash is not null && finalizedHash != Keccak.Zero)
            {
                FinalizedCache? cache = _cache;
                if (cache is not null && cache.Hash == finalizedHash) return cache.Number;

                BlockHeader? finalizedHeader = blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (finalizedHeader is not null)
                {
                    _cache = new FinalizedCache(finalizedHash, finalizedHeader.Number);
                    return finalizedHeader.Number;
                }
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
