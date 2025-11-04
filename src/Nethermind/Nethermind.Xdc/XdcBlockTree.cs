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
    private readonly IXdcConsensusContext xdcConsensus;

    public XdcBlockTree(IXdcConsensusContext xdcConsensus, IBlockStore? blockStore, IHeaderStore? headerDb, [KeyFilter("blockInfos")] IDb? blockInfoDb, [KeyFilter("metadata")] IDb? metadataDb, IBadBlockStore? badBlockStore, IChainLevelInfoRepository? chainLevelInfoRepository, ISpecProvider? specProvider, IBloomStorage? bloomStorage, ISyncConfig? syncConfig, ILogManager? logManager, long genesisBlockNumber = 0) : base(blockStore, headerDb, blockInfoDb, metadataDb, badBlockStore, chainLevelInfoRepository, specProvider, bloomStorage, syncConfig, logManager, genesisBlockNumber)
    {
        this.xdcConsensus = xdcConsensus;
    }

    //protected override bool BestSuggestedImprovementRequirementsSatisfied(BlockHeader header)
    //{
    //    // In XDC we always accept the best suggested improvement
    //    return true;
    //}

}
