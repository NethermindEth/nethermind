// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class XdcBlockTree(
    IXdcConsensusContext xdcConsensus,
    IBlockStore? blockStore,
    IHeaderStore? headerDb,
    [KeyFilter("blockInfos")] IDb? blockInfoDb,
    [KeyFilter("metadata")] IDb? metadataDb,
    IBadBlockStore? badBlockStore,
    IBlockAccessListStore? balStore,
    IChainLevelInfoRepository? chainLevelInfoRepository,
    ISpecProvider? specProvider,
    ISyncConfig? syncConfig,
    IStateBoundary? stateBoundary,
    ILogManager? logManager,
    ulong genesisBlockNumber = 0) : BlockTree(blockStore, headerDb, blockInfoDb, metadataDb, badBlockStore, balStore, chainLevelInfoRepository, specProvider, syncConfig, stateBoundary, logManager, genesisBlockNumber)
{
    private readonly IXdcConsensusContext _xdcConsensus = xdcConsensus;

    protected override AddBlockResult Suggest(Block? block, BlockHeader header, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
    {
        if (!CanAcceptNewBlocks) return AddBlockResult.CannotAccept;

        BlockRoundInfo finalizedBlockInfo = _xdcConsensus.HighestCommitBlock;
        if (finalizedBlockInfo is null)
            return base.Suggest(block, header, options);

        if (finalizedBlockInfo.BlockNumber >= header.Number)
        {
            // Sync can re-suggest finalized blocks while filling gaps.
            return IsKnownBlock(header.Number, header.Hash) && (BestSuggestedHeader?.Number ?? 0) >= header.Number
                ? AddBlockResult.AlreadyKnown
                : AddBlockResult.InvalidBlock;
        }

        BlockHeader current = header;
        while (true)
        {
            if (finalizedBlockInfo.BlockNumber >= current.Number)
                return AddBlockResult.InvalidBlock;

            if (finalizedBlockInfo.Hash == current.ParentHash)
                return base.Suggest(block, header, options);

            current = FindHeader(current.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            if (current is null)
                return AddBlockResult.UnknownParent;
        }
    }

    protected override bool HeadImprovementRequirementsSatisfied(BlockHeader header)
    {
        if (base.HeadImprovementRequirementsSatisfied(header))
            return true;

        return header is XdcBlockHeader newBlock && Head?.Header is XdcBlockHeader headBlock &&
            IsSameTdButPreferred(newBlock, headBlock);
    }

    protected override bool BestSuggestedImprovementRequirementsSatisfied(BlockHeader header)
    {
        if (base.BestSuggestedImprovementRequirementsSatisfied(header))
            return true;

        return header is XdcBlockHeader newBlock && BestSuggestedBody?.Header is XdcBlockHeader bestBlock &&
            IsSameTdButPreferred(newBlock, bestBlock);
    }

    public override bool IsBetterThanHead(BlockHeader? header)
    {
        // XDPoS equal-TD proposals require a round-based tie-break, not base hash ordering.
        if (header is XdcBlockHeader newBlock && Head?.Header is XdcBlockHeader headBlock &&
            newBlock.TotalDifficulty == headBlock.TotalDifficulty)
            return IsSameTdButPreferred(newBlock, headBlock);

        return base.IsBetterThanHead(header);
    }

    internal static bool IsSameTdButPreferred(XdcBlockHeader newHeader, XdcBlockHeader oldHeader)
    {
        if (newHeader.TotalDifficulty != oldHeader.TotalDifficulty)
            return false;

        ulong? newRound = newHeader.ExtraConsensusData?.BlockRound;
        ulong? oldRound = oldHeader.ExtraConsensusData?.BlockRound;
        if (newRound is null || oldRound is null)
            return false;

        return newRound != oldRound ? newRound > oldRound : newHeader.IsSelfMined;
    }
}
