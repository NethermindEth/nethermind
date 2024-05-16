// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Init.Steps;
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

    public RegisterOptimismRpcModules(INethermindApi api, ILogger logger) : base(api)
    {
        _api = (OptimismNethermindApi)api;
        _config = _api.Config<IOptimismConfig>();
        _logger = logger;
    }

    protected override ModuleFactoryBase<IEthRpcModule> CreateEthModuleFactory()
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

        ModuleFactoryBase<IEthRpcModule> ethModuleFactory = base.CreateEthModuleFactory();
        BasicJsonRpcClient? sequencerJsonRpcClient = _config.SequencerUrl is null
            ? null
            : new(new Uri(_config.SequencerUrl), _api.EthereumJsonSerializer, _api.LogManager);

        ITxSigner txSigner = new WalletTxSigner(_api.Wallet, _api.SpecProvider.ChainId);
        TxSealer sealer = new(txSigner, _api.Timestamper);

        return new OptimismEthModuleFactory(ethModuleFactory, sequencerJsonRpcClient, _api.CreateBlockchainBridge(),
            _api.WorldState, _api.EthereumEcdsa, sealer);
    }
}
