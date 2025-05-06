// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Optimism.Rpc;

public class RegisterOptimismRpcModules : RegisterRpcModules
{
    private readonly OptimismNethermindApi _api;
    private readonly ILogger _logger;
    private readonly IOptimismConfig _config;
    private readonly IPoSSwitcher _poSSwitcher;

    public RegisterOptimismRpcModules(INethermindApi api, IPoSSwitcher poSSwitcher) : base(api, poSSwitcher)
    {
        _api = (OptimismNethermindApi)api;
        _poSSwitcher = poSSwitcher;
        _config = _api.Config<IOptimismConfig>();
        _logger = _api.LogManager.GetClassLogger();
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
        StepDependencyException.ThrowIfNull(_api.SpecHelper);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);
        StepDependencyException.ThrowIfNull(_api.Sealer);

        if (_config.SequencerUrl is null && _logger.IsWarn)
        {
            _logger.Warn("SequencerUrl is not set. Nethermind will behave as a Sequencer");
        }

        BasicJsonRpcClient? sequencerJsonRpcClient = _config.SequencerUrl is not null
            ? new(new Uri(_config.SequencerUrl), _api.EthereumJsonSerializer, _api.LogManager)
            : null;

        ITxSigner txSigner = new WalletTxSigner(_api.Wallet, _api.SpecProvider.ChainId);
        TxSealer sealer = new(txSigner, _api.Timestamper);

        var feeHistoryOracle = new FeeHistoryOracle(_api.BlockTree, _api.ReceiptStorage, _api.SpecProvider);
        _api.DisposeStack.Push(feeHistoryOracle);

        ModuleFactoryBase<IOptimismEthRpcModule> optimismEthModuleFactory = new OptimismEthModuleFactory(
            JsonRpcConfig,
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
            sequencerJsonRpcClient,
            _api.EthereumEcdsa,
            sealer,
            _api.SpecHelper);

        _api.OptimismEthRpcModule = optimismEthModuleFactory.Create();

        rpcModuleProvider.RegisterBounded(optimismEthModuleFactory,
            JsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, JsonRpcConfig.Timeout);
    }

    protected override void RegisterTraceRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.L1CostHelper);
        StepDependencyException.ThrowIfNull(_api.SpecHelper);

        IBlocksConfig blockConfig = _api.Config<IBlocksConfig>();
        ulong secondsPerSlot = blockConfig.SecondsPerSlot;

        OptimismTraceModuleFactory traceModuleFactory = new(
            _api.WorldStateManager,
            _api.BlockTree,
            JsonRpcConfig,
            _api.CreateBlockchainBridge(),
            secondsPerSlot,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.SpecProvider,
            _poSSwitcher,
            _api.LogManager,
            _api.L1CostHelper,
            _api.SpecHelper,
            new Create2DeployerContractRewriter(_api.SpecHelper, _api.SpecProvider, _api.BlockTree),
            new BlockProductionWithdrawalProcessor(new NullWithdrawalProcessor()));

        rpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, JsonRpcConfig.Timeout);
    }
}
