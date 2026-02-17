// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class XdcBlockTree : BlockTree
{
    private const int MaxSearchDepth = 1024;
    private readonly IXdcConsensusContext _xdcConsensus;

    public XdcBlockTree(
        IXdcConsensusContext xdcConsensus,
        IBlockStore? blockStore,
        IHeaderStore? headerDb,
        [KeyFilter("blockInfos")] IDb? blockInfoDb,
        [KeyFilter("metadata")] IDb? metadataDb,
        IBadBlockStore? badBlockStore,
        IChainLevelInfoRepository? chainLevelInfoRepository,
        ISpecProvider? specProvider,
        IBloomStorage? bloomStorage,
        ISyncConfig? syncConfig,
        ILogManager? logManager,
        long genesisBlockNumber = 0) : base(blockStore, headerDb, blockInfoDb, metadataDb, badBlockStore, chainLevelInfoRepository, specProvider, bloomStorage, syncConfig, logManager, genesisBlockNumber)
    {
        _xdcConsensus = xdcConsensus;
    }

    protected override AddBlockResult Suggest(Block? block, BlockHeader header, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
    {
        BlockRoundInfo finalizedBlockInfo = _xdcConsensus.HighestCommitBlock;
        if (finalizedBlockInfo is null)
            return base.Suggest(block, header, options);
        if (finalizedBlockInfo.Hash == header.Hash)
        {
            //Weird case if re-suggesting the finalized block
            return AddBlockResult.AlreadyKnown;
        }
        if (finalizedBlockInfo.BlockNumber >= header.Number)
        {
            return AddBlockResult.InvalidBlock;
        }
        if (header.Number - finalizedBlockInfo.BlockNumber > MaxSearchDepth)
        {
            //Theoretically very deep reorgs could happen, if the chain doesn't finalize for a long time
            //TODO Maybe this needs to be revisited later
            Logger.Warn($"Deep reorg past {MaxSearchDepth} blocks detected! Rejecting block {header.ToString(BlockHeader.Format.Full)}");
            return AddBlockResult.InvalidBlock;
        }
        BlockHeader current = header;
        for (long i = header.Number; i >= finalizedBlockInfo.BlockNumber; i--)
        {
            if (finalizedBlockInfo.BlockNumber >= current.Number)
                return AddBlockResult.InvalidBlock;

            if (finalizedBlockInfo.Hash == current.ParentHash)
                return base.Suggest(block, header, options);

            current = FindHeader(current.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            if (current is null)
                return AddBlockResult.UnknownParent;
        }
        //This is not possible to reach
        return AddBlockResult.InvalidBlock;
    }

}
