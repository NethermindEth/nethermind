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
    ILogManager? logManager,
    ulong genesisBlockNumber = 0) : BlockTree(blockStore, headerDb, blockInfoDb, metadataDb, badBlockStore, balStore, chainLevelInfoRepository, specProvider, syncConfig, logManager, genesisBlockNumber)
{
    private readonly IXdcConsensusContext _xdcConsensus = xdcConsensus;

    protected override AddBlockResult Suggest(Block? block, BlockHeader header, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
    {
        if (!CanAcceptNewBlocks) return AddBlockResult.CannotAccept;

        BlockRoundInfo? finalizedBlockInfo = _xdcConsensus.HighestCommitBlock;
        if (finalizedBlockInfo is null)
            return base.Suggest(block, header, options);

        if (finalizedBlockInfo.BlockNumber >= header.Number)
        {
            // During sync, already-finalized blocks may be re-suggested (e.g. gap filling).
            // Accept them as AlreadyKnown instead of treating them as invalid reorg attempts.
            if (header.Hash is null)
                return AddBlockResult.InvalidBlock;

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

            if (current.ParentHash is null)
                return AddBlockResult.UnknownParent;

            BlockHeader? parentHeader = FindHeader(current.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            if (parentHeader is null)
                return AddBlockResult.UnknownParent;

            current = parentHeader;
        }
    }

    protected override bool HeadImprovementRequirementsSatisfied(BlockHeader header)
    {
        if (base.HeadImprovementRequirementsSatisfied(header))
            return true;

        return header is XdcBlockHeader newBlock && Head?.Header is XdcBlockHeader headBlock &&
            IsSameTdButSelfMined(newBlock, headBlock);
    }

    protected override bool BestSuggestedImprovementRequirementsSatisfied(BlockHeader header)
    {
        if (base.BestSuggestedImprovementRequirementsSatisfied(header))
            return true;

        return header is XdcBlockHeader newBlock && BestSuggestedBody?.Header is XdcBlockHeader bestBlock &&
            IsSameTdButSelfMined(newBlock, bestBlock);
    }

    public override bool IsBetterThanHead(BlockHeader? header)
    {
        if (base.IsBetterThanHead(header))
            return true;

        return header is XdcBlockHeader newBlock && Head?.Header is XdcBlockHeader bestBlock &&
            IsSameTdButSelfMined(newBlock, bestBlock);
    }

    // Allow overriding head with self-mined blocks with the same TD
    private static bool IsSameTdButSelfMined(XdcBlockHeader newHeader, XdcBlockHeader oldHeader) =>
        newHeader.TotalDifficulty == oldHeader.TotalDifficulty && newHeader.IsSelfMined;
}
