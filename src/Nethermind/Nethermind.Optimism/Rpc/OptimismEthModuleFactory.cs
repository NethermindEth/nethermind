// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Blockchain.Find;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Optimism.Rpc;

public class OptimismEthModuleFactory(
        IJsonRpcConfig rpcConfig,
        IBlockchainBridgeFactory blockchainBridgeFactory,
        IBlockFinder blockFinder,
        IReceiptFinder receiptFinder,
        IStateReader stateReader,
        ITxPool txPool,
        ITxSender txSender,
        IWallet wallet,
        ILogManager logManager,
        ISpecProvider specProvider,
        IGasPriceOracle gasPriceOracle,
        IEthSyncingInfo ethSyncingInfo,
        IFeeHistoryOracle feeHistoryOracle,
        ulong? secondsPerSlot,
        IJsonRpcClient? sequencerRpcClient,
        IEthereumEcdsa ecdsa,
        ITxSealer sealer,
        IOptimismSpecHelper opSpecHelper
        )
        : ModuleFactoryBase<IOptimismEthRpcModule>
{
    private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
    private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
    private readonly IBlockchainBridgeFactory _blockchainBridgeFactory = blockchainBridgeFactory ?? throw new ArgumentNullException(nameof(blockchainBridgeFactory));
    private readonly ITxPool _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
    private readonly ITxSender _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
    private readonly IWallet _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
    private readonly IJsonRpcConfig _rpcConfig = rpcConfig ?? throw new ArgumentNullException(nameof(rpcConfig));
    private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    private readonly IGasPriceOracle _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
    private readonly IEthSyncingInfo _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
    private readonly IFeeHistoryOracle _feeHistoryOracle = feeHistoryOracle ?? throw new ArgumentNullException(nameof(feeHistoryOracle));
    private readonly IEthereumEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
    private readonly ITxSealer _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
    private readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
    private readonly IReceiptFinder _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
    private readonly IOptimismSpecHelper _opSpecHelper = opSpecHelper ?? throw new ArgumentNullException(nameof(opSpecHelper));

    public override IOptimismEthRpcModule Create()
    {
        return new OptimismEthRpcModule(
            _rpcConfig,
            _blockchainBridgeFactory.CreateBlockchainBridge(),
            _blockFinder,
            _receiptFinder,
            _stateReader,
            _txPool,
            _txSender,
            _wallet,
            _logManager,
            _specProvider,
            _gasPriceOracle,
            _ethSyncingInfo,
            _feeHistoryOracle,
            secondsPerSlot,

            sequencerRpcClient,
            _ecdsa,
            _sealer,
            _opSpecHelper
            );
    }
}
