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
            ILogManager logManager)
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
                logManager)
        {
        }

        public override Block PrepareEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            var block = base.PrepareEmptyBlock(parent, payloadAttributes);

            // processing is only done to apply AuRa block rewards and rewrite posdao contracts
            // TODO should we return null instead of empty block if we couldn't process?
            if (TrySetState(parent.StateRoot))
                block = ProcessPreparedBlock(block, null) ?? block;

            return block;
        }
    }
}
