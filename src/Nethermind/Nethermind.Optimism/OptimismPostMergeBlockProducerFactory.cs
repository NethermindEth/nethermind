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

namespace Nethermind.Optimism;

public class OptimismPostMergeBlockProducerFactory : PostMergeBlockProducerFactory
{
    public OptimismPostMergeBlockProducerFactory(
        ISpecProvider specProvider,
        ISealEngine sealEngine,
        ITimestamper timestamper,
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        IGasLimitCalculator? gasLimitCalculator = null)
        : base(specProvider, sealEngine, timestamper, blocksConfig, logManager, gasLimitCalculator)
    {
    }

    public override PostMergeBlockProducer Create(BlockProducerEnv producerEnv, IBlockProductionTrigger blockProductionTrigger, ITxSource? txSource = null)
    {
        OptimismPayloadTxSource payloadAttrsTxSource = new();

        return new OptimismPostMergeBlockProducer(
            payloadAttrsTxSource,
            new OptimismTxPoolTxSource(txSource ?? producerEnv.TxSource),
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
