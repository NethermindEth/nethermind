// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Taiko;

namespace Nethermind.Optimism.Rpc;

public class RegisterTaikoRpcModules : RegisterRpcModules
{
    private readonly NethermindApi _api;
    private readonly ILogger _logger;
    //private readonly IOptimismConfig _config;
    private readonly IJsonRpcConfig _jsonRpcConfig;

    public RegisterTaikoRpcModules(INethermindApi api) : base(api)
    {
        _api = (NethermindApi)api;
        //_config = _api.Config<IOptimismConfig>();
        _logger = api.LogManager.GetClassLogger();
        _jsonRpcConfig = api.Config<IJsonRpcConfig>();
    }

    //protected override void RegisterEthRpcModule(IRpcModuleProvider rpcModuleProvider)
    //{
    //    StepDependencyException.ThrowIfNull(_api.BlockTree);
    //    StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
    //    StepDependencyException.ThrowIfNull(_api.StateReader);
    //    StepDependencyException.ThrowIfNull(_api.TxPool);
    //    StepDependencyException.ThrowIfNull(_api.TxSender);
    //    StepDependencyException.ThrowIfNull(_api.Wallet);
    //    StepDependencyException.ThrowIfNull(_api.EthSyncingInfo);
    //    StepDependencyException.ThrowIfNull(_api.GasPriceOracle);
    //    StepDependencyException.ThrowIfNull(_api.SpecHelper);
    //    StepDependencyException.ThrowIfNull(_api.SpecProvider);
    //    StepDependencyException.ThrowIfNull(_api.WorldState);
    //    StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);
    //    StepDependencyException.ThrowIfNull(_api.Sealer);

    //    if (_config.SequencerUrl is null && _logger.IsWarn)
    //    {
    //        _logger.Warn("SequencerUrl is not set. Nethermind will behave as a Sequencer");
    //    }

    //    BasicJsonRpcClient? sequencerJsonRpcClient = _config.SequencerUrl is not null
    //        ? new(new Uri(_config.SequencerUrl), _api.EthereumJsonSerializer, _api.LogManager)
    //        : null;

    //    ITxSigner txSigner = new WalletTxSigner(_api.Wallet, _api.SpecProvider.ChainId);
    //    TxSealer sealer = new(txSigner, _api.Timestamper);

    //    var feeHistoryOracle = new FeeHistoryOracle(_api.BlockTree, _api.ReceiptStorage, _api.SpecProvider);
    //    _api.DisposeStack.Push(feeHistoryOracle);

    //    ModuleFactoryBase<IEthRpcModule> optimismEthModuleFactory = new OptimismEthModuleFactory(
    //        _jsonRpcConfig,
    //        _api,
    //        _api.BlockTree.AsReadOnly(),
    //        _api.ReceiptStorage,
    //        _api.StateReader,
    //        _api.TxPool,
    //        _api.TxSender,
    //        _api.Wallet,
    //        _api.LogManager,
    //        _api.SpecProvider,
    //        _api.GasPriceOracle,
    //        _api.EthSyncingInfo,
    //        feeHistoryOracle,
    //        _api.ConfigProvider.GetConfig<IBlocksConfig>().SecondsPerSlot,

    //    sequencerJsonRpcClient,
    //        _api.WorldState,
    //        _api.EthereumEcdsa,
    //        sealer,
    //        _api.SpecHelper);

    //    rpcModuleProvider.RegisterBounded(optimismEthModuleFactory,
    //        _jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, _jsonRpcConfig.Timeout);
    //}

    protected override void RegisterTraceRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.WorldState);
        //StepDependencyException.ThrowIfNull(_api.L1CostHelper);
        //StepDependencyException.ThrowIfNull(_api.SpecHelper);

        TaikoTraceModuleFactory traceModuleFactory = new(
            _api.WorldStateManager,
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

    class TaikoTraceModuleFactory(IWorldStateManager worldStateManager, IBlockTree blockTree, IJsonRpcConfig jsonRpcConfig,
        IBlockPreprocessorStep recoveryStep, IRewardCalculatorSource rewardCalculatorSource, IReceiptStorage receiptFinder,
        ISpecProvider specProvider, IPoSSwitcher poSSwitcher, ILogManager logManager) :
        TraceModuleFactory(worldStateManager, blockTree, jsonRpcConfig, recoveryStep, rewardCalculatorSource, receiptFinder, specProvider, poSSwitcher, logManager)
    {
        protected override ReadOnlyTxProcessingEnv CreateTxProcessingEnv() => new TaikoReadOnlyTxProcessingEnv(_worldStateManager, _blockTree, _specProvider, _logManager);
    }
}
