// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismEthModuleFactory : ModuleFactoryBase<IOptimismEthRpcModule>
{
    private readonly ModuleFactoryBase<IEthRpcModule> _ethModuleFactory;
    private readonly BasicJsonRpcClient? _sequencerRpcClient;
    private readonly IBlockchainBridge _blockchainBridge;
    private readonly IAccountStateProvider _accountStateProvider;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ITxSealer _sealer;

    public OptimismEthModuleFactory(ModuleFactoryBase<IEthRpcModule> ethModuleFactory,
        BasicJsonRpcClient? sequencerRpcClient, IBlockchainBridge blockchainBridge,
        IAccountStateProvider accountStateProvider, IEthereumEcdsa ecdsa, ITxSealer sealer)
    {
        _ethModuleFactory = ethModuleFactory;
        _sequencerRpcClient = sequencerRpcClient;
        _blockchainBridge = blockchainBridge;
        _accountStateProvider = accountStateProvider;
        _ecdsa = ecdsa;
        _sealer = sealer;
    }

    public override IOptimismEthRpcModule Create()
    {
        return new OptimismEthRpcModule(_ethModuleFactory.Create(), _sequencerRpcClient, _blockchainBridge,
            _accountStateProvider, _ecdsa, _sealer);
    }
}
