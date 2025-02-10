// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using System;

namespace Nethermind.Taiko.Rpc;

public class RegisterTaikoRpcModules : RegisterRpcModules
{
    private readonly TaikoNethermindApi _api;
    private readonly ILogger _logger;

    public RegisterTaikoRpcModules(INethermindApi api) : base(api)
    {
        _api = (TaikoNethermindApi)api;
        _logger = api.LogManager.GetClassLogger();
    }

    protected override void RegisterEthRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.StateReader);
        StepDependencyException.ThrowIfNull(_api.TxPool);
        StepDependencyException.ThrowIfNull(_api.TxSender);
        StepDependencyException.ThrowIfNull(_api.Wallet);
        StepDependencyException.ThrowIfNull(_api.EthSyncingInfo);
        StepDependencyException.ThrowIfNull(_api.GasPriceOracle);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.WorldState);
        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);
        StepDependencyException.ThrowIfNull(_api.Sealer);
        StepDependencyException.ThrowIfNull(_api.L1OriginStore);

        ISyncConfig syncConfig = _api.Config<ISyncConfig>();

        StepDependencyException.ThrowIfNull(syncConfig);

        FeeHistoryOracle feeHistoryOracle = new(_api.BlockTree, _api.ReceiptStorage, _api.SpecProvider);
        _api.DisposeStack.Push(feeHistoryOracle);


        ModuleFactoryBase<ITaikoRpcModule> ethModuleFactory = new TaikoEthModuleFactory(
            _jsonRpcConfig,
            _api,
            _api.BlockTree.AsReadOnly(),
            _api.ReceiptStorage,
            _api.StateReader,
            _api.TxPool,
            _api.TxSender,
            _api.Wallet,
            _api.LogManager,
            _api.SpecProvider,
            _api.GasPriceOracle,
            _api.EthSyncingInfo,
            feeHistoryOracle,
            _api.ConfigProvider.GetConfig<IBlocksConfig>().SecondsPerSlot,

            syncConfig,
            _api.L1OriginStore);

        rpcModuleProvider.RegisterBounded(ethModuleFactory,
            _jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, _jsonRpcConfig.Timeout);
    }

    protected override void RegisterTraceRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.WorldState);

        TaikoTraceModuleFactory traceModuleFactory = new(
            _api.StateFactory,
            _api.DbProvider,
            _api.BlockTree,
            _jsonRpcConfig,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.SpecProvider,
            _api.PoSSwitcher,
            _api.LogManager);

        rpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, _jsonRpcConfig.Timeout);
    }
}
