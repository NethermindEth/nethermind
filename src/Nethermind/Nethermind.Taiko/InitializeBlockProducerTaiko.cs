// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Init.Steps;

namespace Nethermind.Taiko;

public class InitializeBlockProducerTaiko(TaikoNethermindApi api) : InitializeBlockProducer(api)
{
    private readonly TaikoNethermindApi _api = api;

    protected override IBlockProducer BuildProducer()
    {
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
        if (_api.ReceiptStorage is null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
        if (_api.TxPool is null) throw new StepDependencyException(nameof(_api.TxPool));
        if (_api.TransactionComparerProvider is null) throw new StepDependencyException(nameof(_api.TransactionComparerProvider));
        if (_api.BlockValidator is null) throw new StepDependencyException(nameof(_api.BlockValidator));
        if (_api.WorldStateManager is null) throw new StepDependencyException(nameof(_api.WorldStateManager));
        if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));

        _api.BlockProducerEnvFactory = new TaikoBlockProducerEnvFactory(
            _api.WorldStateManager,
            _api.BlockTree,
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.BlockPreprocessor,
            _api.TxPool,
            _api.TransactionComparerProvider,
            _api.Config<IBlocksConfig>(),
            _api.LogManager,
            _api.EthereumEcdsa);

        _api.GasLimitCalculator = new TaikoGasLimitCalculator();
        _api.BlockProducer = new FailBlockProducer();

        return _api.BlockProducer;
    }
}
