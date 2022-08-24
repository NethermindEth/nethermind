using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.AuRa
{
    public class AuRaPostMergeBlockProducerFactory : PostMergeBlockProducerFactory
    {
        public AuRaPostMergeBlockProducerFactory(
            ISpecProvider specProvider,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            IMiningConfig miningConfig,
            ILogManager logManager,
            IGasLimitCalculator? gasLimitCalculator = null)
            : base(
                specProvider,
                sealEngine,
                timestamper,
                miningConfig,
                logManager,
                gasLimitCalculator)
        {
        }

        public override PostMergeBlockProducer Create(
            BlockProducerEnv producerEnv,
            IBlockProductionTrigger blockProductionTrigger,
            ITxSource? txSource = null)
        {
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator =
                new(_specProvider, _miningConfig);

            return new AuRaPostMergeBlockProducer(
                txSource ?? producerEnv.TxSource,
                producerEnv.ChainProcessor,
                producerEnv.BlockTree,
                blockProductionTrigger,
                producerEnv.ReadOnlyStateProvider,
                _gasLimitCalculator ?? targetAdjustedGasLimitCalculator,
                _sealEngine,
                _timestamper,
                _specProvider,
                _logManager);
        }
    }
}
