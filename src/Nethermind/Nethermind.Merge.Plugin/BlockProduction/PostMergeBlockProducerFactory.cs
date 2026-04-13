// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public class PostMergeBlockProducerFactory(
        ISpecProvider specProvider,
        ISealEngine sealEngine,
        ITimestamper timestamper,
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        IGasLimitCalculator? gasLimitCalculator = null)
    {
        protected readonly ISpecProvider _specProvider = specProvider;
        protected readonly ISealEngine _sealEngine = sealEngine;
        protected readonly ITimestamper _timestamper = timestamper;
        protected readonly IBlocksConfig _blocksConfig = blocksConfig;
        protected readonly ILogManager _logManager = logManager;
        protected readonly IGasLimitCalculator? _gasLimitCalculator = gasLimitCalculator;

        public virtual PostMergeBlockProducer Create(
            IBlockProducerEnv producerEnv,
            ITxSource? txSource = null)
        {

            return new PostMergeBlockProducer(
                txSource ?? producerEnv.TxSource,
                producerEnv.ChainProcessor,
                producerEnv.BlockTree,
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
