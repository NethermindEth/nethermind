// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.State.Repositories;

namespace Nethermind.State.Pbt;

/// <summary>
/// Finds the header of the block a scope is about to fold, so the scope can report the state root
/// that header claims rather than the EIP-8297 root it computed.
/// </summary>
/// <remarks>
/// PBT is not the consensus tree: a chain's headers commit to a Patricia root, and a block whose
/// header carries anything else fails validation. The backend still does the whole stem-tree fold and
/// keeps its own root — see <see cref="PbtSnapshot.TreeRoot"/> — but everything that addresses a
/// state, this <see cref="StateId"/> included, goes by the header's root, which is what this resolves.
/// </remarks>
public interface IPbtChildHeaderSource
{
    /// <summary>The header of a block whose parent is <paramref name="parent"/>, or null when no such block is known.</summary>
    BlockHeader? TryFindChild(BlockHeader parent);
}

/// <summary>Resolves nothing, leaving the scope to report its own EIP-8297 root.</summary>
/// <remarks>
/// For scopes whose blocks are never in the block tree — <c>eth_call</c> state overrides,
/// <c>eth_simulateV1</c> — where a lookup by height would answer with the canonical child's root
/// instead, and where nothing validates the root anyway.
/// </remarks>
public sealed class NullPbtChildHeaderSource : IPbtChildHeaderSource
{
    public static readonly NullPbtChildHeaderSource Instance = new();

    private NullPbtChildHeaderSource()
    {
    }

    public BlockHeader? TryFindChild(BlockHeader parent) => null;
}

/// <inheritdoc cref="IPbtChildHeaderSource"/>
public class PbtBlockTreeChildHeaderSource(IBlockTree blockTree, IChainLevelInfoRepository chainLevelInfoRepository) : IPbtChildHeaderSource
{
    /// <remarks>
    /// Scans the level for the block that names <paramref name="parent"/>, rather than asking for the
    /// canonical block at that height: the block being processed is suggested but not yet canonical,
    /// and a post-merge lookup by height answers null for a level with no canonical block. Matching
    /// on the parent hash also keeps fork siblings apart, which a height alone cannot.
    /// </remarks>
    public BlockHeader? TryFindChild(BlockHeader parent)
    {
        ulong childNumber = parent.Number + 1;
        ChainLevelInfo? level = chainLevelInfoRepository.LoadLevel(childNumber);
        if (level is null) return null;

        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            BlockHeader? child = blockTree.FindHeader(
                blockInfo.BlockHash,
                BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing,
                blockNumber: childNumber);
            if (child?.ParentHash == parent.Hash) return child;
        }

        return null;
    }
}
