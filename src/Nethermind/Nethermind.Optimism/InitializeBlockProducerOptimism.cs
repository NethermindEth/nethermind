// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Init.Steps;

namespace Nethermind.Optimism;

public class InitializeBlockProducerOptimism : InitializeBlockProducer
{
    public InitializeBlockProducerOptimism(OptimismNethermindApi api) : base(api) { }

    protected override Task<IBlockProducer> BuildProducer()
    {
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.ReadOnlyTrieStore is null) throw new StepDependencyException(nameof(_api.ReadOnlyTrieStore));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
        if (_api.ReceiptStorage is null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
        if (_api.TxPool is null) throw new StepDependencyException(nameof(_api.TxPool));
        if (_api.TransactionComparerProvider is null) throw new StepDependencyException(nameof(_api.TransactionComparerProvider));
        if (_api.BlockValidator is null) throw new StepDependencyException(nameof(_api.BlockValidator));

        _api.BlockProducerEnvFactory = new OptimismBlockProducerEnvFactory(
            _api.ChainSpec,
            _api.DbProvider,
            _api.BlockTree,
            _api.ReadOnlyTrieStore,
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.BlockPreprocessor,
            _api.TxPool,
            _api.TransactionComparerProvider,
            _api.Config<IBlocksConfig>(),
            _api.LogManager);

        _api.GasLimitCalculator = new OptimismGasLimitCalculator();
        BlockProducerEnv producerEnv = _api.BlockProducerEnvFactory.Create();

        _api.BlockProducer = new OptimismPostMergeBlockProducer(
            new OptimismPayloadTxSource(),
            producerEnv.TxSource,
            producerEnv.ChainProcessor,
            producerEnv.BlockTree,
            _api.ManualBlockProductionTrigger,
            producerEnv.ReadOnlyStateProvider,
            _api.GasLimitCalculator,
            NullSealEngine.Instance,
            new ManualTimestamper(),
            _api.SpecProvider,
            _api.LogManager,
            _api.Config<IBlocksConfig>());

        return Task.FromResult(_api.BlockProducer);
    }
}
