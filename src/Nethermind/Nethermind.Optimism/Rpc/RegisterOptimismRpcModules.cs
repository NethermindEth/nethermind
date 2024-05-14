// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Optimism;

public class RegisterOptimismRpcModules : RegisterRpcModules
{
    private readonly OptimismNethermindApi _api;

    public RegisterOptimismRpcModules(INethermindApi api) : base(api)
    {
        _api = (OptimismNethermindApi)api;
    }

    protected override ModuleFactoryBase<IEthRpcModule> CreateEthModuleFactory()
    {
        StepDependencyException.ThrowIfNull(_api.SpecHelper);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.WorldState);
        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);
        StepDependencyException.ThrowIfNull(_api.Sealer);
        ModuleFactoryBase<IEthRpcModule> ethModuleFactory = base.CreateEthModuleFactory();
        BasicJsonRpcClient sequencerJsonRpcClient = new(new Uri(_api.SpecHelper.SequencerUrl), _api.EthereumJsonSerializer, _api.LogManager);

        ITxSigner txSigner = new WalletTxSigner(_api.Wallet, _api.SpecProvider.ChainId);
        TxSealer sealer = new(txSigner, _api.Timestamper);

        return new OptimismEthModuleFactory(ethModuleFactory, sequencerJsonRpcClient, _api.CreateBlockchainBridge(),
            _api.WorldState, _api.EthereumEcdsa, sealer);
    }
}
