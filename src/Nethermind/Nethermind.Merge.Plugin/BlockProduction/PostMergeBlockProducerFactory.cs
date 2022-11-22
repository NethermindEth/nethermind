// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

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
        private readonly ISpecProvider _specProvider;
        private readonly ISealEngine _sealEngine;
        private readonly ITimestamper _timestamper;
        private readonly IMiningConfig _miningConfig;
        private readonly ILogManager _logManager;
        private readonly IGasLimitCalculator? _gasLimitCalculator;

        public PostMergeBlockProducerFactory(
            ISpecProvider specProvider,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            IMiningConfig miningConfig,
            ILogManager logManager,
            IGasLimitCalculator? gasLimitCalculator = null)
        {
            _specProvider = specProvider;
            _sealEngine = sealEngine;
            _timestamper = timestamper;
            _miningConfig = miningConfig;
            _logManager = logManager;
            _gasLimitCalculator = gasLimitCalculator;
        }

        public PostMergeBlockProducer Create(
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
                _gasLimitCalculator ?? new TargetAdjustedGasLimitCalculator(_specProvider, _miningConfig),
                _sealEngine,
                _timestamper,
                _specProvider,
                _logManager,
                _miningConfig);
        }
    }
}
