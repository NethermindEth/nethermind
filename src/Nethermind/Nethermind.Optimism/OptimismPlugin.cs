// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.JsonRpc.Modules;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Optimism.CL;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Rlp;
using Nethermind.Optimism.Rpc;
using Nethermind.Optimism.ProtocolVersion;
using Nethermind.Optimism.Cl.Rpc;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Optimism.Precompiles;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism;

public class OptimismPlugin(ChainSpec chainSpec, IOptimismConfig optimismConfig) : IConsensusPlugin
{
    public string Author => "Nethermind";
    public string Name => "Optimism";
    public string Description => "Optimism support for Nethermind";

    private OptimismNethermindApi? _api;
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    #region IConsensusPlugin

    public string SealEngineType => Core.SealEngineType.Optimism;

    #endregion

    public void InitTxTypesAndRlpDecoders(INethermindApi api)
    {
        api.RegisterTxType<DepositTransactionForRpc>(new OptimismTxDecoder<Transaction>(), Always.Valid);
        api.RegisterTxType<LegacyTransactionForRpc>(new OptimismLegacyTxDecoder(), new OptimismLegacyTxValidator(api.SpecProvider!.ChainId));
        Rlp.RegisterDecoders(typeof(OptimismReceiptMessageDecoder).Assembly, true);
    }

    public Task Init(INethermindApi api)
    {
        _api = (OptimismNethermindApi)api;

        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);

        return Task.CompletedTask;
    }

    public bool MustInitialize => true;

    public Type ApiType => typeof(OptimismNethermindApi);

    public IModule Module => new OptimismModule(chainSpec, optimismConfig);
}

public class OptimismModule(ChainSpec chainSpec, IOptimismConfig optimismConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<NethermindApi, OptimismNethermindApi>()
            .AddModule(new BaseMergePluginModule())
            .AddModule(new OptimismSynchronizerModule(chainSpec))

            .AddSingleton(chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<OptimismChainSpecEngineParameters>())
            .AddSingleton<IOptimismSpecHelper, OptimismSpecHelper>()
            .AddSingleton<ICostHelper, OptimismCostHelper>()

            .AddSingleton<ISpecProvider, OptimismChainSpecBasedSpecProvider>()
            .AddSingleton<IPrecompileProvider, OptimismPrecompileProvider>()
            .AddSingleton<IRpcCapabilitiesProvider, OptimismEngineRpcCapabilitiesProvider>()

            .AddSingleton<OptimismBlockProducerFactory>()
            .Bind<IBlockProducerFactory, OptimismBlockProducerFactory>()
            .Bind<IBlockProducerRunnerFactory, OptimismBlockProducerFactory>()
            .AddSingleton<IBlockProductionPolicy>(AlwaysStartBlockProductionPolicy.Instance)

            .AddSingleton<IPoSSwitcher, OptimismPoSSwitcher>()
            .AddSingleton<IGossipPolicy>(ShouldNotGossip.Instance)
            .AddSingleton<StartingSyncPivotUpdater, UnsafeStartingSyncPivotUpdater>()

            // Step override
            .AddStep(typeof(InitializeBlockchainOptimism))

            // Validators
            .AddSingleton<IBlockValidator, OptimismBlockValidator>()
            .AddSingleton<IHeaderValidator, OptimismHeaderValidator>()
            .AddSingleton<IUnclesValidator>(Always.Valid)

            // Block processing
            .AddScoped<ITransactionProcessor, OptimismTransactionProcessor>()
            .AddScoped<IBlockProcessor, OptimismBlockProcessor>()
            .AddScoped<IWithdrawalProcessor, OptimismWithdrawalProcessor>()
            .AddSingleton<IWithdrawalProcessorFactory, OptimismWithdrawalProcessorFactory>()
            .AddScoped<Create2DeployerContractRewriter>()
            .AddScoped<BlockProcessor.IBlockProductionTransactionPicker, ISpecProvider, IBlocksConfig>((specProvider, blocksConfig) =>
                new OptimismBlockProductionTransactionPicker(specProvider, blocksConfig.BlockProductionMaxTxKilobytes))

            .AddDecorator<IEthereumEcdsa, OptimismEthereumEcdsa>()
            .AddDecorator<IBlockProducerTxSourceFactory, OptimismBlockProducerTxSourceFactory>()
            .AddSingleton<IPayloadPreparationService, OptimismPayloadPreparationService>()
            .AddScoped<IGenesisPostProcessor, OptimismGenesisPostProcessor>()

            // Rpcs
            .AddSingleton<IHealthHintService, IBlocksConfig>((blocksConfig) =>
                new ManualHealthHintService(blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint))

            .AddSingleton<OptimismEthModuleFactory>()
                .Bind<IRpcModuleFactory<IOptimismEthRpcModule>, OptimismEthModuleFactory>()
                .Bind<IRpcModuleFactory<IEthRpcModule>, OptimismEthModuleFactory>()

            .AddSingleton<IOptimismSignalSuperchainV1Handler, ILogManager>(logManager =>
                new LoggingOptimismSignalSuperchainV1Handler(OptimismConstants.CurrentProtocolVersion, logManager))
            .RegisterSingletonJsonRpcModule<IOptimismEngineRpcModule, OptimismEngineRpcModule>()
            ;

        if (optimismConfig.ClEnabled)
        {
            builder
                .Map<CLChainSpecEngineParameters, ChainSpec>(cs => cs.EngineChainSpecParametersProvider
                    .GetChainSpecParameters<CLChainSpecEngineParameters>())

                .AddSingleton<IEthApi, IJsonSerializer, ILogManager>((jsonSerializer, logManager) =>
                    new EthereumEthApi(optimismConfig.L1EthApiEndpoint!, jsonSerializer, logManager))
                .AddSingleton<IBeaconApi, IJsonSerializer, IEthereumEcdsa, ILogManager>((jsonSerializer, ethereumEcdsa, logManager) =>
                    new EthereumBeaconApi(new Uri(optimismConfig.L1BeaconApiEndpoint!), jsonSerializer, ethereumEcdsa, logManager))

                .AddSingleton<IDecodingPipeline, DecodingPipeline>()
                .AddSingleton<IL1Bridge, EthereumL1Bridge>()
                .AddSingleton<IL1ConfigValidator, L1ConfigValidator>()
                .AddSingleton<ISystemConfigDeriver, CLChainSpecEngineParameters>(clParameters =>
                    new SystemConfigDeriver(clParameters.SystemConfigProxy!))
                // Single L2-facing eth module instance for internal CL use (distinct from the eth_ request pool).
                .AddSingleton<IOptimismEthRpcModule>(ctx =>
                    ctx.Resolve<IRpcModuleFactory<IOptimismEthRpcModule>>().Create())
                .AddSingleton<IL2Api, L2Api>()
                .AddSingleton<IExecutionEngineManager, ExecutionEngineManager>()
                .AddSingleton<OptimismCL>()

                .RegisterSingletonJsonRpcModule<IOptimismOptimismRpcModule, OptimismOptimismRpcModule>()

                // Starts the CL driver (resolved above; disposed by the container).
                .AddStep(typeof(StartOptimismCl));
        }
    }
}
