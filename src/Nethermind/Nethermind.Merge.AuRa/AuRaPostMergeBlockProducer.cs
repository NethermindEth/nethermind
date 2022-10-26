using Nethermind.Blockchain;
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
            IStateProvider stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager,
            IMiningConfig miningConfig)
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
                miningConfig)
        {
        }

        public override Block PrepareEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            var block = base.PrepareEmptyBlock(parent, payloadAttributes);

            if (TrySetState(parent.StateRoot))
                return ProcessPreparedBlock(block, null) ?? throw new System.Exception("Couldn't process empty block");

            throw new System.Exception("Couldn't process empty block");
        }
    }
}
