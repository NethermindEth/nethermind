// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;

namespace Nethermind.Blockchain.Blocks;

/// <summary>The one place a block may go without its EIP-8288 dependency proof: at or below the finalized block.</summary>
/// <remarks>EIPs#12549 and EIP-8498 let a node that trusts finality skip the proofs of finalized blocks. Sync does not fetch
/// them and validation accepts them without one; every other block needs its proof.</remarks>
public static class DependencyProofFinality
{
    /// <summary>Whether <paramref name="header"/> is at or below the finalized block, so its proof is not needed.</summary>
    public static bool IsTrusted(IBlockFinder blockFinder, BlockHeader header) =>
        blockFinder.FindFinalizedHeader() is { } finalized && header.Number <= finalized.Number;
}
