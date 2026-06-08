// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Rlp;
using Autofac;
using Autofac.Core;
using Nethermind.Init.Modules;
using Nethermind.Core.Container;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Serialization.Json;
using Nethermind.Taiko.BlockTransactionExecutors;
using Nethermind.Taiko.Config;
using Nethermind.Taiko.Rpc;
using Nethermind.Taiko.Tdx;
using Nethermind.Taiko.Precompiles;
using Nethermind.Taiko.TaikoSpec;
using Nethermind.Taiko.ZkGas;

namespace Nethermind.Taiko;

public class TaikoPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public const string Taiko = "Taiko";
    public string Author => "Nethermind";
    public string Name => Taiko;
    public string Description => "Taiko support for Nethermind";

    private TaikoNethermindApi? _api;
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    public Task Init(INethermindApi api)
    {
        _api = (TaikoNethermindApi)api;

        _api.FinalizationManager = new ManualBlockFinalizationManager();

        _api.GossipPolicy = ShouldNotGossip.Instance;

        _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_api.Context.Resolve<IPoSSwitcher>()));

        InitializeL1Precompiles();

        return Task.CompletedTask;
    }

    private void InitializeL1Precompiles()
    {
        ArgumentNullException.ThrowIfNull(_api?.SpecProvider);

        if (_api.SpecProvider.GetFinalSpec() is not TaikoReleaseSpec taikoSpec)
            throw new InvalidOperationException("TaikoPlugin requires TaikoChainSpecBasedSpecProvider");

        ILogManager logManager = _api.Context.Resolve<ILogManager>();
        ILogger logger = logManager.GetClassLogger<TaikoPlugin>();

        bool sloadEnabled = taikoSpec.IsRip7728Enabled;
        bool staticCallEnabled = taikoSpec.IsL1StaticCallEnabled;

        if (logger.IsInfo) logger.Info($"L1SLOAD (RIP-7728): {(sloadEnabled ? "enabled" : "disabled")}");
        if (logger.IsInfo) logger.Info($"L1STATICCALL: {(staticCallEnabled ? "enabled" : "disabled")}");

        if (!sloadEnabled && !staticCallEnabled)
            return;

        ISurgeConfig surgeConfig = _api.Context.Resolve<ISurgeConfig>();

        if (string.IsNullOrEmpty(surgeConfig.L1EthApiEndpoint))
            throw new ArgumentException($"{nameof(surgeConfig.L1EthApiEndpoint)} must be provided in the Surge configuration to use L1 precompiles");

        if (logger.IsInfo) logger.Info($"L1 precompiles: using L1 endpoint: {surgeConfig.L1EthApiEndpoint}");

        // Single RPC client shared by both L1 precompile providers. Process-lifetime scope.
        IJsonRpcClient l1RpcClient = new BasicJsonRpcClient(
            new Uri(surgeConfig.L1EthApiEndpoint),
            _api.Context.Resolve<IJsonSerializer>(),
            logManager,
            L1PrecompileConstants.L1RpcTimeout);
        _api.DisposeStack.Push((IDisposable)l1RpcClient);

        if (sloadEnabled)
        {
            L1SloadPrecompile.L1StorageProvider = new JsonRpcL1StorageProvider(l1RpcClient, logManager);
            L1SloadPrecompile.Logger = logManager.GetClassLogger<L1SloadPrecompile>();
            if (logger.IsInfo) logger.Info("L1SLOAD: precompile initialized");
        }

        if (staticCallEnabled)
        {
            L1StaticCallPrecompile.L1CallProvider = new JsonRpcL1CallProvider(l1RpcClient, logManager);
            L1StaticCallPrecompile.Logger = logManager.GetClassLogger<L1StaticCallPrecompile>();
            if (logger.IsInfo) logger.Info("L1STATICCALL: precompile initialized");
        }
    }

    public bool MustInitialize => true;

    public string SealEngineType => Core.SealEngineType.Taiko;

    public IModule Module => new TaikoModule();

    public Type ApiType => typeof(TaikoNethermindApi);
}

public class TaikoModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<NethermindApi, TaikoNethermindApi>()
            .AddModule(new BaseMergePluginModule())
            .AddModule(new TaikoSynchronizerModule())

            .AddSingleton<IPrecompileProvider, TaikoPrecompileProvider>()
            .AddScoped<IVirtualMachine, TaikoEthereumVirtualMachine>()
            .AddSingleton<ISpecProvider, TaikoChainSpecBasedSpecProvider>()
            .Map<TaikoChainSpecEngineParameters, ChainSpec>(chainSpec =>
                chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<TaikoChainSpecEngineParameters>())

            // Steps override
            .AddStep(typeof(InitializeBlockchainTaiko))

            // L1 origin store
            .AddSingleton<RlpDecoder<L1Origin>, L1OriginDecoder>()
            .AddDatabase(L1OriginStore.L1OriginDbName, L1OriginStore.L1OriginDbName, L1OriginStore.L1OriginDbName.ToLower())
            .AddSingleton<IL1OriginStore, L1OriginStore>()

            // Sync modification
            .AddSingleton<IPoSSwitcher>(AlwaysPoS.Instance)
            .AddSingleton<StartingSyncPivotUpdater, UnsafeStartingSyncPivotUpdater>()

            // Validators
            .AddSingleton<IBlockValidator, TaikoBlockValidator>()
            .AddSingleton<IHeaderValidator, TaikoHeaderValidator>()
            .AddSingleton<IUnclesValidator>(Always.Valid)

            // Block processing
            .AddSingleton<IBlockValidationModule, TaikoBlockValidationModule>()
            .AddSingleton<IMainProcessingModule, TaikoMainBlockProcessingModule>()
            .AddScoped<ITransactionProcessor, TaikoTransactionProcessor>()
            .AddScoped<ZkGasMeterHolder>()
            .AddScoped<IBlockProcessor, TaikoBlockProcessor>()
            .AddScoped<IExecutionRequestsProcessor, TaikoExecutionRequestsProcessor>()
            .AddScoped<IBlockProducerEnvFactory, TaikoBlockProductionEnvFactory>()

            .AddSingleton<IRlpDecoder<Transaction>>((_) => TxDecoder.Instance)
            .AddSingleton<IPayloadPreparationService, IBlockProducerEnvFactory, L1OriginStore, ISpecProvider, IRlpDecoder<Transaction>, ILogManager>(CreatePayloadPreparationService)
            .AddSingleton<IHealthHintService, IBlocksConfig>(blocksConfig =>
                new ManualHealthHintService(blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint))

            // Conditionally register SurgeGasPriceOracle if UseSurgeGasPriceOracle is enabled
            .AddDecorator<IGasPriceOracle>((ctx, defaultGasPriceOracle) =>
            {
                ISpecProvider specProvider = ctx.Resolve<ISpecProvider>();
                TaikoReleaseSpec taikoSpec = (TaikoReleaseSpec)specProvider.GenesisSpec;

                if (!taikoSpec.UseSurgeGasPriceOracle)
                    return defaultGasPriceOracle;

                ISurgeConfig surgeConfig = ctx.Resolve<ISurgeConfig>();

                if (string.IsNullOrEmpty(surgeConfig.L1EthApiEndpoint))
                {
                    throw new ArgumentException("L1EthApiEndpoint must be provided in the Surge configuration to compute the gas price");
                }

                if (string.IsNullOrEmpty(surgeConfig.TaikoInboxAddress))
                {
                    throw new ArgumentException("TaikoInboxAddress must be provided in the Surge configuration to compute the gas price");
                }

                BasicJsonRpcClient l1RpcClient = new(
                    new Uri(surgeConfig.L1EthApiEndpoint),
                    ctx.Resolve<IJsonSerializer>(),
                    ctx.Resolve<ILogManager>());

                return new SurgeGasPriceOracle(
                    ctx.Resolve<IBlockFinder>(),
                    ctx.Resolve<ILogManager>(),
                    specProvider,
                    ctx.Resolve<IBlocksConfig>().MinGasPrice,
                    l1RpcClient,
                    surgeConfig);
            })

            // Override GetPayloadV2 to skip fork validation and carry headerDifficulty via blockValue
            .AddSingleton<IAsyncHandler<byte[], GetPayloadV2Result?>, TaikoGetPayloadV2Handler>()

            // Rpc
            .RegisterSingletonJsonRpcModule<ITaikoExtendedEthRpcModule, TaikoExtendedEthModule>()
            .RegisterSingletonJsonRpcModule<ITaikoEngineRpcModule, TaikoEngineRpcModule>()
                .AddSingleton<IForkchoiceUpdatedHandler, TaikoForkchoiceUpdatedHandler>()

            // TDX attestation (enabled with Surge.TdxEnabled) 
            .AddModule(new TdxModule())

            // Need to set the rlp globally
            .OnBuild(ctx =>
            {
                Rlp.RegisterDecoder(typeof(L1Origin), ctx.Resolve<RlpDecoder<L1Origin>>());
            })
            ;
    }

    private static IPayloadPreparationService CreatePayloadPreparationService(
        IBlockProducerEnvFactory blockProducerEnvFactory,
        L1OriginStore l1OriginStore,
        ISpecProvider specProvider,
        IRlpDecoder<Transaction> txDecoder,
        ILogManager logManager)
    {
        IBlockProducerEnv blockProducerEnv = blockProducerEnvFactory.CreatePersistent();

        TaikoPayloadPreparationService payloadPreparationService = new(
            blockProducerEnv.ChainProcessor,
            blockProducerEnv.ReadOnlyStateProvider,
            l1OriginStore,
            specProvider,
            logManager,
            txDecoder);

        return payloadPreparationService;
    }

    private class TaikoBlockValidationModule : Module, IBlockValidationModule
    {
        protected override void Load(ContainerBuilder builder) => builder.AddScoped<IBlockProcessor.IBlockTransactionsExecutor, TaikoBlockValidationTransactionExecutor>();
    }

    private class TaikoMainBlockProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) => builder
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockInvalidTxExecutor>()
            // Register GenesisBuilder by its concrete type so TaikoGenesisBuilder can inject
            // it directly without resolving through IGenesisBuilder (which would cause a cycle).
            .AddScoped<GenesisBuilder>()
            .AddScoped<IGenesisBuilder>(static ctx =>
                new TaikoGenesisBuilder(ctx.Resolve<GenesisBuilder>(), ctx.Resolve<ISpecProvider>()));
    }

}
