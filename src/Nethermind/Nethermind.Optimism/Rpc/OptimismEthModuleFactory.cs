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
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.Network;
using Nethermind.Serialization.Json;

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
    private readonly IOptimismConfig _config;
    private readonly ILogger _logger;

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
        _config = config;
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        _blockchainBridgeFactory = blockchainBridgeFactory ?? throw new ArgumentNullException(nameof(blockchainBridgeFactory));
        _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        _rpcConfig = rpcConfig ?? throw new ArgumentNullException(nameof(rpcConfig));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
        _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
        _feeHistoryOracle = feeHistoryOracle ?? throw new ArgumentNullException(nameof(feeHistoryOracle));
        _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        _opSpecHelper = opSpecHelper ?? throw new ArgumentNullException(nameof(opSpecHelper));
        _protocolsManager = protocolsManager ?? throw new ArgumentNullException(nameof(protocolsManager));
        _logger = logManager.GetClassLogger<OptimismEthModuleFactory>();

        if (_config.SequencerUrl is null && _logger.IsWarn)
        {
            _logger.Warn("SequencerUrl is not set. Nethermind will behave as a Sequencer");
        }
        BasicJsonRpcClient? sequencerJsonRpcClient = _config.SequencerUrl is not null
            ? new(new Uri(_config.SequencerUrl), jsonSerializer, logManager)
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
