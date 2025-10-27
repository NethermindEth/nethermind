// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain;

public class ReorgDepthFinalizedStateProvvider(IBlockTree blockTree): IFinalizedStateProvider
{
    public long FinalizedBlockNumber => blockTree.BestKnownNumber - Reorganization.MaxDepth;
    public Hash256? GetFinalizedStateRootAt(long blockNumber)
    {
        BlockHeader? header = blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        return header?.StateRoot;
    }
}
