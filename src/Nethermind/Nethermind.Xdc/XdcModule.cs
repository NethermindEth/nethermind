// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Abi;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;
using Nethermind.Xdc.Discovery;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

public class XdcModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddDecorator<IRocksDbConfigFactory, XdcRocksDbConfigFactory>() // Register custom RocksDb config factory that handles XdcSnapshots without validation
            .AddProtocolHandler<P2P.XdcProtocolHandler>() // Register XDC protocol handler using clean DSL (intercepts ETH protocol version 100)
            .AddStep(typeof(InitializeBlockchainXdc))
            .Intercept<ChainSpec>(XdcChainSpecLoader.ProcessChainSpec)
            .AddSingleton<ISpecProvider, XdcChainSpecBasedSpecProvider>()
            .Map<XdcChainSpecEngineParameters, ChainSpec>(chainSpec =>
                chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>())

            .AddDecorator<IGenesisBuilder, XdcGenesisBuilder>()
            .AddScoped<IBlockProcessor, XdcBlockProcessor>()

            .Add<StartXdcBlockProducer>()
            .AddSingleton<XdcBlockProducerFactory>()
            .Bind<IBlockProducerFactory, XdcBlockProducerFactory>()
            .Bind<IBlockProducerRunnerFactory, XdcBlockProducerFactory>()


            // stores
            .AddSingleton<IHeaderStore, XdcHeaderStore>()
            .AddSingleton<IXdcHeaderStore, XdcHeaderStore>()
            .AddSingleton<IBlockStore, XdcBlockStore>()
            .AddSingleton<IBlockTree, XdcBlockTree>()

            // Sys contracts
            //TODO this might not be wired correctly
            .AddSingleton<
                IMasternodeVotingContract,
                IAbiEncoder,
                ISpecProvider,
                IReadOnlyTxProcessingEnvFactory>(CreateVotingContract)
            .AddSingleton<IMintedRecordContract, MintedRecordContract>()

            // sealer
            .AddSingleton<ISealer, XdcSealer>()

            // signing transactions cache
            .AddSingleton<ISigningTxCache, SigningTxCache>()

            // penalty handler
            .AddSingleton<IPenaltyHandler, PenaltyHandler>()

            // reward handler
            .AddDecorator<IRewardCalculatorSource, XdcRewardCalculatorSource>()

            // forensics handler
            .AddSingleton<IForensicsProcessor, NullForensicsProcessor>()

            // Validators
            .AddSingleton<IBlockValidator, XdcBlockValidator>()
            .AddSingleton<IHeaderValidator, XdcHeaderValidator>()
            .AddSingleton<ISealValidator, XdcSealValidator>()
            .AddSingleton<IUnclesValidator, MustBeEmptyUnclesValidator>()

            // managers
            .AddSingleton<IMasternodesCalculator, MasternodesCalculator>()
            .AddSingleton<IVotesManager, VotesManager>()
            .AddSingleton<IQuorumCertificateManager, QuorumCertificateManager>()
            .AddSingleton<ITimeoutCertificateManager, TimeoutCertificateManager>()
            .AddSingleton<IEpochSwitchManager, EpochSwitchManager>()
            .AddSingleton<IXdcConsensusContext, XdcConsensusContext>()
            .AddDatabase(XdcRocksDbConfigFactory.XdcSnapshotDbName)
            .AddDatabase(XdcRocksDbConfigFactory.XdcRewardsDbName)
            .AddSingleton<IPenaltyHandler, PenaltyHandler>()
            .AddSingleton<IRewardsStore, RewardsStore>()
            .AddSingleton<ITimeoutTimer, TimeoutTimer>()
            .AddSingleton<ISyncInfoManager, SyncInfoManager>()

            // sync
            .AddSingleton<IBeaconSyncStrategy, XdcBeaconSyncStrategy>()
            .AddSingleton<IPeerAllocationStrategyFactory<StateSyncBatch>, XdcStateSyncAllocationStrategyFactory>()
            .AddSingleton<IXdcStateSyncSnapshotManager, XdcStateSyncSnapshotManager>()
            .AddSingleton<IStateSyncPivot, XdcStateSyncPivot>()
            .AddSingleton<ISyncDownloader<StateSyncBatch>, XdcStateSyncDownloader>()


            //Network
            .AddSingleton<IProtocolValidator, XdcProtocolValidator>()
            .AddSingleton<IHeaderDecoder, XdcHeaderDecoder>()
            .AddSingleton(new BlockDecoder(new XdcHeaderDecoder()))
            .AddMessageSerializer<VoteMsg, VoteMsgSerializer>()
            .AddMessageSerializer<SyncInfoMsg, SyncInfoMsgSerializer>()
            .AddMessageSerializer<TimeoutMsg, TimeoutMsgSerializer>()
            .AddMessageSerializer<PingMsg, XdcPingMsgSerializer>()

            .AddLast<ITxGossipPolicy, XdcTxGossipPolicy>()
            .AddLast<IP2PCapabilityResolver, XdcP2PCapabilityResolver>()
            .RemoveOrderedComponents<IP2PCapabilityResolver, DefaultP2PCapabilityResolver>()
            .AddSingleton<IBlockProducerTxSourceFactory, XdcTxPoolTxSourceFactory>()

            // block processing
            .AddScoped<ITransactionProcessor, XdcTransactionProcessor>()
            .AddSingleton<IGasLimitCalculator, XdcGasLimitCalculator>()
            .AddSingleton<IDifficultyCalculator, XdcDifficultyCalculator>()
            .AddScoped<IProducedBlockSuggester, XdcBlockSuggester>()

            .RegisterSingletonJsonRpcModule<IXdcRpcModule, XdcRpcModule>();

        builder.RegisterType<SnapshotManager>().As<ISnapshotManager>().WithAttributeFiltering().SingleInstance();
        builder.RegisterType<SignTransactionManager>().As<ISignTransactionManager>().As<IStartable>().SingleInstance();

        // Overrides DiscoveryApp registered in DiscoveryModule (via NethermindModule).
        // Safe: plugins are always loaded after NethermindModule in NethermindRunnerModule, so last-registration-wins is guaranteed.
        builder.RegisterType<XdcDiscoveryApp>().As<DiscoveryApp>().WithAttributeFiltering().SingleInstance().ExternallyOwned();
    }

    private IMasternodeVotingContract CreateVotingContract(
        IAbiEncoder abiEncoder,
        ISpecProvider specProvider,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnv)
    {
        IXdcReleaseSpec spec = (XdcReleaseSpec)specProvider.GetFinalSpec();
        return new MasternodeVotingContract(abiEncoder, spec.MasternodeVotingContract, readOnlyTxProcessingEnv);
    }
}
