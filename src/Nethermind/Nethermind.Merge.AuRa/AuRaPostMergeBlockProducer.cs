// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.State;

namespace Nethermind.Merge.AuRa
{
    public class AuRaPostMergeBlockProducer : PostMergeBlockProducer
    {
        public AuRaPostMergeBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProductionTrigger blockProductionTrigger,
            IWorldState stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlocksConfig blocksConfig)
            : base(
                txSource,
                processor,
                blockTree,
                blockProductionTrigger,
                stateProvider,
                gasLimitCalculator,
                sealEngine,
                timestamper,
                specProvider,
                logManager,
                blocksConfig)
        {
        }

        public override Block PrepareEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            var block = base.PrepareEmptyBlock(parent, payloadAttributes);

            if (TrySetState(parent.StateRoot))
                return ProcessPreparedBlock(block, null) ?? throw new EmptyBlockProductionException("Block processing failed");

            throw new EmptyBlockProductionException("Setting state for processing block failed");
        }
    }
}
