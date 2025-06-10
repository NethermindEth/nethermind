// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Facade;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class RegisterAuRaRpcModules : RegisterRpcModules
{

    public RegisterAuRaRpcModules(AuRaNethermindApi api, IAuRaBlockProcessorFactory auRaBlockProcessorFactory, IPoSSwitcher poSSwitcher) : base(api, poSSwitcher)
    {
        _api = api;
        _factory = auRaBlockProcessorFactory;
    }

    private static AuRaNethermindApi _api = null!;
    private readonly IAuRaBlockProcessorFactory _factory;

    protected override void RegisterDebugRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockPreprocessor);
        StepDependencyException.ThrowIfNull(_api.BlockValidator);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.KeyStore);
        StepDependencyException.ThrowIfNull(_api.BadBlocksStore);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);

        IBlocksConfig blockConfig = _api.Config<IBlocksConfig>();
        ulong secondsPerSlot = blockConfig.SecondsPerSlot;

        AuRaDebugModuleFactory debugModuleFactory = new(
            _api.WorldStateManager,
            _api.DbProvider,
            _api.BlockTree,
            JsonRpcConfig,
            _api.CreateBlockchainBridge(),
            secondsPerSlot,
            _api.BlockValidator,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            new ReceiptMigration(_api),
            _api.ConfigProvider,
            _api.SpecProvider,
            _api.SyncModeSelector,
            _api.BadBlocksStore,
            _api.FileSystem,
            _api.LogManager,
            _factory);

        rpcModuleProvider.RegisterBoundedByCpuCount(debugModuleFactory, JsonRpcConfig.Timeout);
    }

    protected class AuRaDebugModuleFactory(
        IWorldStateManager worldStateManager,
        IDbProvider dbProvider,
        IBlockTree blockTree,
        IJsonRpcConfig jsonRpcConfig,
        IBlockchainBridge blockchainBridge,
        ulong secondsPerSlot,
        IBlockValidator blockValidator,
        IBlockPreprocessorStep recoveryStep,
        IRewardCalculatorSource rewardCalculator,
        IReceiptStorage receiptStorage,
        IReceiptsMigration receiptsMigration,
        IConfigProvider configProvider,
        ISpecProvider specProvider,
        ISyncModeSelector syncModeSelector,
        IBadBlockStore badBlockStore,
        IFileSystem fileSystem,
        ILogManager logManager,
        IAuRaBlockProcessorFactory factory)
        : DebugModuleFactory(worldStateManager, dbProvider, blockTree, jsonRpcConfig, blockchainBridge, secondsPerSlot, blockValidator, recoveryStep,
            rewardCalculator, receiptStorage, receiptsMigration, configProvider, specProvider, syncModeSelector,
            badBlockStore, fileSystem, logManager)
    {
        protected override ReadOnlyChainProcessingEnv CreateReadOnlyChainProcessingEnv(IReadOnlyTxProcessingScope scope,
            IOverridableWorldScope worldStateManager, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor)
        {
            return new AuRaReadOnlyChainProcessingEnv(
                _api,
                scope,
                Always.Valid,
                _recoveryStep,
                _rewardCalculatorSource.Get(scope.TransactionProcessor),
                _receiptStorage,
                _specProvider,
                _blockTree,
                worldStateManager.GlobalStateReader,
                _logManager,
                transactionsExecutor,
                factory);
        }
    }

}

public interface IAuRaBlockProcessorFactory
{
    public AuRaBlockProcessor Create(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        IBeaconBlockRootHandler beaconBlockRootHandler,
        ILogManager logManager,
        IBlockFinder blockTree,
        IWithdrawalProcessor withdrawalProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IAuRaValidator? auRaValidator,
        ITxFilter? txFilter = null,
        AuRaContractGasLimitOverride? gasLimitOverride = null,
        ContractRewriter? contractRewriter = null,
        IBlockCachePreWarmer? preWarmer = null);
}

public class AuRaBlockProcessorFactory : IAuRaBlockProcessorFactory
{
    public AuRaBlockProcessor Create(ISpecProvider specProvider, IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator, IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor, IWorldState stateProvider,
        IReceiptStorage receiptStorage, IBeaconBlockRootHandler beaconBlockRootHandler, ILogManager logManager,
        IBlockFinder blockTree, IWithdrawalProcessor withdrawalProcessor, IExecutionRequestsProcessor executionRequestsProcessor,
        IAuRaValidator? auRaValidator, ITxFilter? txFilter = null, AuRaContractGasLimitOverride? gasLimitOverride = null,
        ContractRewriter? contractRewriter = null, IBlockCachePreWarmer? preWarmer = null) =>
        new(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            beaconBlockRootHandler,
            logManager,
            blockTree,
            withdrawalProcessor,
            executionRequestsProcessor,
            auRaValidator,
            txFilter,
            gasLimitOverride,
            contractRewriter, preWarmer);
}

public class AutoAuRaTraceModuleFactory(IWorldStateManager worldStateManager, Func<ICodeInfoRepository> codeInfoRepositoryFunc, ILifetimeScope rootLifetimeScope) : AutoTraceModuleFactory(worldStateManager, codeInfoRepositoryFunc, rootLifetimeScope)
{
    protected override ContainerBuilder ConfigureCommonBlockProcessing<T>(
        ContainerBuilder builder,
        ICodeInfoRepository codeInfoRepository,
        IWorldState worldState
    )
    {
        // Screw it! aura will just construct things on its own.
        return base.ConfigureCommonBlockProcessing<T>(builder, codeInfoRepository, worldState)
            .AddScoped<IReadOnlyTxProcessingScope, ITransactionProcessor, IWorldState>((txP, worldState) => new ReadOnlyTxProcessingScope(txP, worldState, Keccak.EmptyTreeHash))
            .AddScoped<ReadOnlyChainProcessingEnv, AuRaReadOnlyChainProcessingEnv>()
            .AddScoped<IBlockProcessor, ReadOnlyChainProcessingEnv>((env) => env.BlockProcessor);
    }
}

public class AuRaReadOnlyChainProcessingEnv(
    AuRaNethermindApi _api,
    IReadOnlyTxProcessingScope scope,
    IBlockValidator blockValidator,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculator rewardCalculator,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IStateReader stateReader,
    ILogManager logManager,
    IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
    IAuRaBlockProcessorFactory factory)
    : ReadOnlyChainProcessingEnv(scope, blockValidator, recoveryStep, rewardCalculator, receiptStorage,
        specProvider, blockTree, stateReader, logManager, blockTransactionsExecutor)
{
    AuRaChainSpecEngineParameters _parameters = _api.ChainSpec.EngineChainSpecParametersProvider
        .GetChainSpecParameters<AuRaChainSpecEngineParameters>();
    IAuraConfig _auraConfig = _api.Config<IAuraConfig>();

    protected override IBlockProcessor CreateBlockProcessor(IReadOnlyTxProcessingScope scope, IBlockTree blockTree,
        IBlockValidator blockValidator, IRewardCalculator rewardCalculator, IReceiptStorage receiptStorage,
        ISpecProvider specProvider, ILogManager logManager, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor)
    {
        ITxFilter auRaTxFilter = new ServiceTxFilter(specProvider);

        var chainSpecAuRa = _api.ChainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();
        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = chainSpecAuRa.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        return factory.Create(
            specProvider!,
            blockValidator!,
            rewardCalculator,
            transactionsExecutor,
            scope.WorldState,
            receiptStorage,
            new BeaconBlockRootHandler(scope.TransactionProcessor, scope.WorldState),
            logManager,
            blockTree,
            NullWithdrawalProcessor.Instance,
            new ExecutionRequestsProcessor(scope.TransactionProcessor),
            auRaValidator: null,
            auRaTxFilter,
            GetGasLimitCalculator(),
            contractRewriter
        );
    }

    private AuRaContractGasLimitOverride? GetGasLimitCalculator()
    {
        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
        var blockGasLimitContractTransitions = _parameters.BlockGasLimitContractTransitions;

        if (blockGasLimitContractTransitions?.Any() == true)
        {
            AuRaContractGasLimitOverride gasLimitCalculator = new(
                blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                        new BlockGasLimitContract(
                            _api!.AbiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            _api!.ReadOnlyTxProcessingEnvFactory.Create()))
                    .ToArray<IBlockGasLimitContract>(),
                _api.GasLimitCalculatorCache,
                _auraConfig.Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
                new TargetAdjustedGasLimitCalculator(_api.SpecProvider, _api.Config<IBlocksConfig>()),
                _api.LogManager);

            return gasLimitCalculator;
        }

        // do not return target gas limit calculator here - this is used for validation to check if the override should have been used
        return null;
    }
}
