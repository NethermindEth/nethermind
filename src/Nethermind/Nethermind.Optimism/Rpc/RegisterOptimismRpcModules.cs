// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Optimism;

public class RegisterOptimismRpcModules : RegisterRpcModules
{
    private readonly OptimismNethermindApi _api;
    private readonly ILogger _logger;
    private readonly IOptimismConfig _config;
    private readonly IJsonRpcConfig _jsonRpcConfig;

    public RegisterOptimismRpcModules(INethermindApi api) : base(api)
    {
        _api = (OptimismNethermindApi)api;
        _config = _api.Config<IOptimismConfig>();
        _logger = _api.LogManager.GetClassLogger();
        _jsonRpcConfig = _api.Config<IJsonRpcConfig>();
    }

    protected override void RegisterEthRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.SpecHelper);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.WorldState);
        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);
        StepDependencyException.ThrowIfNull(_api.Sealer);

        if (_config.SequencerUrl is null && _logger.IsWarn)
        {
            _logger.Warn($"SequencerUrl is not set.");
        }

        BasicJsonRpcClient? sequencerJsonRpcClient = _config.SequencerUrl is null
            ? null
            : new(new Uri(_config.SequencerUrl), _api.EthereumJsonSerializer, _api.LogManager);
        ModuleFactoryBase<IEthRpcModule> ethModuleFactory = CreateEthModuleFactory();

        ITxSigner txSigner = new WalletTxSigner(_api.Wallet, _api.SpecProvider.ChainId);
        TxSealer sealer = new(txSigner, _api.Timestamper);

        ModuleFactoryBase<IOptimismEthRpcModule> optimismEthModuleFactory = new OptimismEthModuleFactory(
            ethModuleFactory, sequencerJsonRpcClient, _api.CreateBlockchainBridge(), _api.WorldState, _api.EthereumEcdsa, sealer, _api.BlockTree?.AsReadOnly(), _api.SpecProvider, _api.ReceiptFinder, _api.SpecHelper);

        rpcModuleProvider.RegisterBounded(optimismEthModuleFactory,
            _jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, _jsonRpcConfig.Timeout);
    }
}
