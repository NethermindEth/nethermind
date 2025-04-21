// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.HealthChecks;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.WurdumTestPlugin;

public class WurdumPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public string Name => "WurdumTestPlugin";
    public string Description => "Wurdum test plugin";
    public string Author => "Wurdum";
    public bool Enabled => chainSpec.SealEngineType == WurdumChainSpecEngineParameters.WurdumEngineName;

    private INethermindApi? _api;

    public IEnumerable<StepInfo> GetSteps()
    {
        yield return typeof(WurdumInitializeBlockchainStep);
    }

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        ArgumentNullException.ThrowIfNull(_api);
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        ArgumentNullException.ThrowIfNull(_api.SpecProvider);

        _api!.RpcModuleProvider.Register(new SingletonModulePool<IWurdumRpcModule>(
            new WurdumRpcModule(_api.ManualBlockProductionTrigger, _api.ChainSpec, _api.LogManager.GetClassLogger<WurdumRpcModule>())));

        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        /*_api.FinalizationManager = new ManualBlockFinalizationManager();
        _api.RewardCalculatorSource = NoBlockRewards.Instance;
        _api.SealValidator = NullSealEngine.Instance;
        _api.GossipPolicy = ShouldNotGossip.Instance;*/

        return Task.CompletedTask;
    }

    public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
    {
        StepDependencyException.ThrowIfNull(_api);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.BlockValidator);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.TxPool);
        StepDependencyException.ThrowIfNull(_api.TransactionComparerProvider);

        _api.BlockProducerEnvFactory = new BlockProducerEnvFactory(
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
            _api.LogManager);

        var producerEnv = _api.BlockProducerEnvFactory.Create();

        return new PostMergeBlockProducer(
            new WurdumRpcTxSource(),
            producerEnv.ChainProcessor,
            producerEnv.BlockTree,
            producerEnv.ReadOnlyStateProvider,
            new WurdumGasLimitCalculator(),
            NullSealEngine.Instance,
            new ManualTimestamper(),
            _api.SpecProvider,
            _api.LogManager,
            _api.Config<IBlocksConfig>());
    }

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        StepDependencyException.ThrowIfNull(_api);
        StepDependencyException.ThrowIfNull(_api.BlockTree);

        return new StandardBlockProducerRunner(_api.ManualBlockProductionTrigger, _api.BlockTree, blockProducer);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

public class WurdumGasLimitCalculator : IGasLimitCalculator
{
    public long GetGasLimit(BlockHeader parentHeader) => long.MaxValue;
}
