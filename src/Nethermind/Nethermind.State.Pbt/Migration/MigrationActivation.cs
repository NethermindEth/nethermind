// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Answers where the chain stands relative to the EIP-8347 activation.</summary>
public static class MigrationActivation
{
    /// <summary>True once a post-activation block is finalized: the transition window is closed and the MPT may be dropped.</summary>
    public static bool IsFinal(IBlockTree blockTree, ISpecProvider specProvider) =>
        blockTree.FindFinalizedHeader() is { } finalized && specProvider.GetSpec(finalized).IsEip8347Enabled;

    /// <summary>The last pre-activation ancestor of <paramref name="header"/>, or null when it is genesis-less or unreachable.</summary>
    public static BlockHeader? FindActivationParent(IBlockTree blockTree, ISpecProvider specProvider, BlockHeader header, BlockTreeLookupOptions options)
    {
        BlockHeader? cursor = header;
        while (cursor is not null && specProvider.GetSpec(cursor).IsEip8347Enabled)
            cursor = cursor.IsGenesis ? null : blockTree.FindHeader(cursor.ParentHash!, options);
        return cursor;
    }

    /// <summary>Whether PBT, rather than the flat MPT, serves a scope: the one rule that splits the migration's native backends.</summary>
    /// <remarks>
    /// The backend is chosen by the block about to be executed (or, for a pure read, by the block whose state is
    /// read): PBT once EIP-8347 is active, the flat MPT before. Pre-activation, main processing runs on flat alone and
    /// the BAL followers bring PBT up to the blocks it processes (see <see cref="PbtBranchFollower"/>).
    /// </remarks>
    public static bool IsBinary(ISpecProvider specProvider, BlockHeader? baseBlock, BlockHeader? targetBlock)
    {
        BlockHeader? header = targetBlock ?? baseBlock;
        return (header is null ? specProvider.GenesisSpec : specProvider.GetSpec(header)).IsEip8347Enabled;
    }
}
