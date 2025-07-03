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
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.State;

namespace Nethermind.Optimism.Rpc;

public class OptimismEthModuleFactory : ModuleFactoryBase<IOptimismEthRpcModule>
{
    private readonly ILogManager _logManager;
    private readonly IStateReader _stateReader;
    private readonly IBlockchainBridgeFactory _blockchainBridgeFactory;
    private readonly ITxPool _txPool;
    private readonly ITxSender _txSender;
    private readonly IWallet _wallet;
    private readonly IJsonRpcConfig _rpcConfig;
    private readonly ISpecProvider _specProvider;
    private readonly IGasPriceOracle _gasPriceOracle;
    private readonly IEthSyncingInfo _ethSyncingInfo;
    private readonly IFeeHistoryOracle _feeHistoryOracle;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ITxSealer _sealer;
    private readonly IBlockFinder _blockFinder;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IOptimismSpecHelper _opSpecHelper;
    private readonly IProtocolsManager _protocolsManager;
    private readonly ulong? _secondsPerSlot;
    private readonly IJsonRpcClient? _sequencerRpcClient;

    public OptimismEthModuleFactory(IJsonRpcConfig rpcConfig,
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
        IProtocolsManager protocolsManager,
        IBlocksConfig blocksConfig,
        IEthereumEcdsa ecdsa,
        IOptimismSpecHelper opSpecHelper,
        IOptimismConfig config,
        IJsonSerializer jsonSerializer,
        ITimestamper timestamper
    )
    {
        _secondsPerSlot = blocksConfig.SecondsPerSlot;
        _logManager = logManager;
        _stateReader = stateReader;
        _blockchainBridgeFactory = blockchainBridgeFactory;
        _txPool = txPool;
        _txSender = txSender;
        _wallet = wallet;
        _rpcConfig = rpcConfig;
        _specProvider = specProvider;
        _gasPriceOracle = gasPriceOracle;
        _ethSyncingInfo = ethSyncingInfo;
        _feeHistoryOracle = feeHistoryOracle;
        _ecdsa = ecdsa;
        _blockFinder = blockFinder;
        _receiptFinder = receiptFinder;
        _opSpecHelper = opSpecHelper;
        _protocolsManager = protocolsManager;

        ILogger logger = logManager.GetClassLogger<OptimismEthModuleFactory>();
        if (config.SequencerUrl is null && logger.IsWarn)
        {
            logger.Warn("SequencerUrl is not set. Nethermind will behave as a Sequencer");
        }

        BasicJsonRpcClient? sequencerJsonRpcClient = config.SequencerUrl is not null
            ? new(new Uri(config.SequencerUrl), jsonSerializer, logManager)
            : null;
        _sequencerRpcClient = sequencerJsonRpcClient;

        ITxSigner txSigner = new WalletTxSigner(wallet, specProvider.ChainId);
        TxSealer sealer = new(txSigner, timestamper);
        _sealer = sealer;
    }

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
            _protocolsManager,
            _secondsPerSlot,

            _sequencerRpcClient,
            _ecdsa,
            _sealer,
            _opSpecHelper
        );
    }
}
