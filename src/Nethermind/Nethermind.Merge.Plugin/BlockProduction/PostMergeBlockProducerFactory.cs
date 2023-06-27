// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public class PostMergeBlockProducerFactory
    {
        protected readonly ISpecProvider _specProvider;
        protected readonly ISealEngine _sealEngine;
        protected readonly ITimestamper _timestamper;
        protected readonly IBlocksConfig _blocksConfig;
        protected readonly ILogManager _logManager;
        protected readonly IGasLimitCalculator? _gasLimitCalculator;

        public PostMergeBlockProducerFactory(
            ISpecProvider specProvider,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            IBlocksConfig blocksConfig,
            ILogManager logManager,
            IGasLimitCalculator? gasLimitCalculator = null)
        {
            _specProvider = specProvider;
            _sealEngine = sealEngine;
            _timestamper = timestamper;
            _blocksConfig = blocksConfig;
            _logManager = logManager;
            _gasLimitCalculator = gasLimitCalculator;
        }

        public virtual PostMergeBlockProducer Create(
            BlockProducerEnv producerEnv,
            IBlockProductionTrigger blockProductionTrigger,
            ITxSource? txSource = null)
        {
            return new PostMergeBlockProducer(
                txSource ?? producerEnv.TxSource,
                producerEnv.ChainProcessor,
                producerEnv.BlockTree,
                blockProductionTrigger,
                producerEnv.ReadOnlyStateProvider,
                _gasLimitCalculator ?? new TargetAdjustedGasLimitCalculator(_specProvider, _blocksConfig),
                _sealEngine,
                _timestamper,
                _specProvider,
                _logManager,
                _blocksConfig);
        }
    }
}
