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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class XdcBlockTree : BlockTree
{
    private const int MaxSearchDepth = 1024;
    private readonly XdcContext xdcConsensus;

    public XdcBlockTree(XdcContext xdcConsensus, IBlockStore? blockStore, IHeaderStore? headerDb, [KeyFilter("blockInfos")] IDb? blockInfoDb, [KeyFilter("metadata")] IDb? metadataDb, IBadBlockStore? badBlockStore, IChainLevelInfoRepository? chainLevelInfoRepository, ISpecProvider? specProvider, IBloomStorage? bloomStorage, ISyncConfig? syncConfig, ILogManager? logManager, long genesisBlockNumber = 0) : base(blockStore, headerDb, blockInfoDb, metadataDb, badBlockStore, chainLevelInfoRepository, specProvider, bloomStorage, syncConfig, logManager, genesisBlockNumber)
    {
        this.xdcConsensus = xdcConsensus;
    }
    protected override bool BestSuggestedImprovementRequirementsSatisfied(BlockHeader header)
    {
        Types.BlockRoundInfo finalizedBlockInfo = xdcConsensus.HighestCommitBlock;
        if (finalizedBlockInfo is null)
            return true;
        if (finalizedBlockInfo.Hash == header.Hash)
        {
            //Weird case if re-suggesting the finalized block
            return false;
        }
        ChainLevelInfo finalizedLevel = ChainLevelInfoRepository.LoadLevel(finalizedBlockInfo.BlockNumber);
        int depth = 0;
        BlockHeader current = header;
        while (true)
        {
            if (finalizedBlockInfo.Hash == current.ParentHash)
                return base.BestSuggestedImprovementRequirementsSatisfied(header);

            //Basically just an optimization in case this header is on a different chain than finalized
            if (finalizedLevel.BlockInfos.Any(b => b.BlockHash == current.ParentHash))
                return false;

            current = FindHeader(current.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            if (current == null)
                return false;
            depth++;
            if(depth == MaxSearchDepth)
            {
                //Theoretically very deep reorgs could happen, if the chain doesnt finalize for a long time
                //TODO Maybe this needs to be revisited later
                Logger.Warn($"Deep reorg past {MaxSearchDepth} blocks detected! Rejecting block {header.ToString(BlockHeader.Format.Full)}");
                return false;
            }
        }
    }

}
