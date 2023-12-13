// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
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
            IBlocksConfig blocksConfig,
            ILogManager logManager,
            IGasLimitCalculator? gasLimitCalculator = null)
            : base(
                specProvider,
                sealEngine,
                timestamper,
                blocksConfig,
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
                new(_specProvider, _blocksConfig);

            return new PostMergeBlockProducer(
                txSource ?? producerEnv.TxSource,
                producerEnv.ChainProcessor,
                producerEnv.BlockTree,
                blockProductionTrigger,
                producerEnv.ReadOnlyStateProvider,
                _gasLimitCalculator ?? targetAdjustedGasLimitCalculator,
                _sealEngine,
                _timestamper,
                _specProvider,
                _logManager,
                _blocksConfig);
        }
    }
}
