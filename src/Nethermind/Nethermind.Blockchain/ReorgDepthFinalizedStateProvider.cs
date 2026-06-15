// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain;

public class ReorgDepthFinalizedStateProvider(IBlockTree blockTree) : IFinalizedStateProvider
{
    public ulong FinalizedBlockNumber => blockTree.BestKnownNumber >= Reorganization.MaxDepth ? blockTree.BestKnownNumber - Reorganization.MaxDepth : 0UL;

    public Hash256? GetFinalizedStateRootAt(ulong blockNumber)
    {
        if (FinalizedBlockNumber < blockNumber) return null;
        return blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical)?.StateRoot;
    }
}
