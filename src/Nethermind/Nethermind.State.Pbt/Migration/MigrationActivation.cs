// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Answers where the chain stands relative to the EIP-8347 activation.</summary>
internal static class MigrationActivation
{
    /// <summary>True once a post-activation block is finalized: the transition window is closed and the MPT may be dropped.</summary>
    public static bool IsFinal(IBlockTree blockTree, ISpecProvider specProvider) =>
        blockTree.FinalizedHash is { } finalizedHash && finalizedHash != Hash256.Zero &&
        blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.None) is { } finalized && specProvider.GetSpec(finalized).IsEip8347Enabled;

    /// <summary>The last pre-activation ancestor of <paramref name="header"/>, or null when it is genesis-less or unreachable.</summary>
    public static BlockHeader? FindActivationParent(IBlockTree blockTree, ISpecProvider specProvider, BlockHeader header)
    {
        BlockHeader? cursor = header;
        while (cursor is not null && specProvider.GetSpec(cursor).IsEip8347Enabled)
            cursor = cursor.IsGenesis ? null : blockTree.FindHeader(cursor.ParentHash!, BlockTreeLookupOptions.None);
        return cursor;
    }
}
