// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Blocks;

/// <summary>The one place a block may go without its EIP-8288 dependency proof: the finalized block and its canonical ancestors.</summary>
/// <remarks>EIPs#12549 and EIP-8498 let a node that trusts finality skip the proofs of finalized blocks. Sync does not fetch
/// them and validation accepts them without one; every other block, a side-chain block below the finalized height
/// included, needs its proof.</remarks>
public static class DependencyProofFinality
{
    /// <summary>Whether <paramref name="header"/> is the finalized block or one of its ancestors, so its proof is not needed.</summary>
    /// <remarks>Below the finalized height a block is an ancestor when its level's main-chain block and beacon main-chain block,
    /// whichever are set, are both this block. A level where they disagree is not trusted, so the proof is fetched.</remarks>
    public static bool IsTrusted(IBlockTree blockTree, BlockHeader header)
    {
        if (header.Hash is not { } hash || blockTree.FindFinalizedHeader() is not { } finalized || header.Number > finalized.Number)
            return false;

        if (hash == finalized.Hash) return true;
        if (header.Number == finalized.Number) return false;

        ChainLevelInfo? level = blockTree.FindLevel(header.Number);
        if (level is null) return false;

        // The main chain and a beacon-sync mark can disagree at a height, either while beacon sync moves to a new branch or
        // after processing moved on from an old mark; neither is then known to lead to the finalized block.
        Hash256? beacon = null;
        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            if (blockInfo.IsBeaconMainChain) beacon ??= blockInfo.BlockHash;
        }

        Hash256? main = level.MainChainBlock?.BlockHash;
        return (main ?? beacon) == hash && (beacon is null || beacon == hash);
    }
}
