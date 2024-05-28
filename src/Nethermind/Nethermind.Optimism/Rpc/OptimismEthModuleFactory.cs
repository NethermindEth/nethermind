// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.TxPool;
using System;

namespace Nethermind.Optimism;

public class OptimismEthModuleFactory : ModuleFactoryBase<IOptimismEthRpcModule>
{
    private readonly ModuleFactoryBase<IEthRpcModule> _ethModuleFactory;
    private readonly BasicJsonRpcClient? _sequencerRpcClient;
    private readonly IBlockchainBridge _blockchainBridge;
    private readonly IAccountStateProvider _accountStateProvider;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ITxSealer _sealer;
    private readonly IBlockFinder _blockFinder;
    private readonly ISpecProvider _specProvider;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IOPConfigHelper _opConfigHelper;

    public OptimismEthModuleFactory(ModuleFactoryBase<IEthRpcModule> ethModuleFactory,
        BasicJsonRpcClient? sequencerRpcClient, IBlockchainBridge blockchainBridge,
        IAccountStateProvider accountStateProvider, IEthereumEcdsa ecdsa, ITxSealer sealer,
        IBlockFinder? blockFinder,
        ISpecProvider specProvider,
        IReceiptFinder? receiptFinder,
        IOPConfigHelper opConfigHelper)
    {
        _ethModuleFactory = ethModuleFactory;
        _sequencerRpcClient = sequencerRpcClient;
        _blockchainBridge = blockchainBridge;
        _accountStateProvider = accountStateProvider;
        _ecdsa = ecdsa;
        _sealer = sealer;
        _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        _specProvider = specProvider;
        _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        _opConfigHelper = opConfigHelper;
    }

    public override IOptimismEthRpcModule Create()
    {
        return new OptimismEthRpcModule(_ethModuleFactory.Create(), _sequencerRpcClient, _blockchainBridge,
            _accountStateProvider, _ecdsa, _sealer, _blockFinder, _specProvider, _receiptFinder, _opConfigHelper);
    }
}
