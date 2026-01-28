// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Abi;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.Spec;
using Nethermind.State;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Xdc;

public class XdcModule : Module
{
    private const string SnapshotDbName = "Snapshots";

    protected override void Load(ContainerBuilder builder)
    {
        builder.AddStep(typeof(XdcInitializeNetwork));

        base.Load(builder);

        builder
            .AddStep(typeof(InitializeBlockchainXdc))
            .Intercept<ChainSpec>(XdcChainSpecLoader.ProcessChainSpec)
            .AddSingleton<ISpecProvider, XdcChainSpecBasedSpecProvider>()
            .Map<XdcChainSpecEngineParameters, ChainSpec>(chainSpec =>
                chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>())

            .AddDecorator<IGenesisBuilder, XdcGenesisBuilder>()
            .AddScoped<IBlockProcessor, XdcBlockProcessor>()

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
                IReadOnlyTxProcessingEnvFactory,
                ITransactionProcessor>(CreateVotingContract)

            // sealer
            .AddSingleton<ISealer, XdcSealer>()

            // penalty handler

            // reward handler
            .AddSingleton<IRewardCalculator, XdcRewardCalculator>()


            // forensics handler
            .AddSingleton<IForensicsProcessor, ForensicsProcessor>()

            // Validators
            .AddSingleton<IHeaderValidator, XdcHeaderValidator>()
            .AddSingleton<ISealValidator, XdcSealValidator>()
            .AddSingleton<IUnclesValidator, MustBeEmptyUnclesValidator>()

            // managers
            .AddSingleton<IVotesManager, VotesManager>()
            .AddSingleton<IQuorumCertificateManager, QuorumCertificateManager>()
            .AddSingleton<ITimeoutCertificateManager, TimeoutCertificateManager>()
            .AddSingleton<IEpochSwitchManager, EpochSwitchManager>()
            .AddSingleton<IXdcConsensusContext, XdcConsensusContext>()
            .AddDatabase(SnapshotDbName)
            .AddSingleton<ISnapshotManager, IDb, IBlockTree, IPenaltyHandler, IMasternodeVotingContract, ISpecProvider>(CreateSnapshotManager)
            .AddSingleton<ISignTransactionManager, ISigner, ITxPool, ILogManager>(CreateSignTransactionManager)
            .AddSingleton<IPenaltyHandler, PenaltyHandler>()
            .AddSingleton<ITimeoutTimer, TimeoutTimer>()
            .AddSingleton<ISyncInfoManager, SyncInfoManager>()

            //Network
            .AddSingleton<IProtocolValidator, XdcProtocolValidator>()
            .AddSingleton<IHeaderDecoder, XdcHeaderDecoder>()
            .AddSingleton(new BlockDecoder(new XdcHeaderDecoder()))


            .AddSingleton<IBlockProducerTxSourceFactory, XdcTxPoolTxSourceFactory>()

            // block processing
            .AddScoped<ITransactionProcessor, XdcTransactionProcessor>()

            //Sync
            .AddSingleton<CreateSnapshotOnStateSyncFinished>()
                .OnActivate<ISyncFeed<StateSyncBatch>>((_, ctx) =>
                {
                    ctx.Resolve<CreateSnapshotOnStateSyncFinished>();
                })
            ;
    }

    private ISnapshotManager CreateSnapshotManager([KeyFilter(SnapshotDbName)] IDb db, IBlockTree blockTree, IPenaltyHandler penaltyHandler, IMasternodeVotingContract votingContract, ISpecProvider specProvider)
    {
        return new SnapshotManager(db, blockTree, penaltyHandler, votingContract, specProvider);
    }
    private ISignTransactionManager CreateSignTransactionManager(ISigner signer, ITxPool txPool, ILogManager logManager)
    {
        return new SignTransactionManager(signer, txPool, logManager.GetClassLogger<SignTransactionManager>());
    }

    private IMasternodeVotingContract CreateVotingContract(
        IAbiEncoder abiEncoder,
        ISpecProvider specProvider,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnv,
        ITransactionProcessor transactionProcessor)
    {
        IXdcReleaseSpec spec = (XdcReleaseSpec)specProvider.GetFinalSpec();
        return new MasternodeVotingContract(abiEncoder, spec.MasternodeVotingContract, readOnlyTxProcessingEnv, transactionProcessor);
    }
}
