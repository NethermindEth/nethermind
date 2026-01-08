// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Logging;

namespace Nethermind.Xdc;

public class XdcModule : Module
{
    private const string SnapshotDbName = "Snapshots";

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
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
                IReadOnlyTxProcessingEnvFactory>(CreateVotingContract)

            // sealer
            .AddSingleton<ISealer, XdcSealer>()

            // penalty handler

            // reward handler
            .AddSingleton<IRewardCalculator, XdcRewardCalculator>()

            // forensics handler

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
            .AddSingleton<IPenaltyHandler, PenaltyHandler>()
            .AddSingleton<ITimeoutTimer, TimeoutTimer>()
            .AddSingleton<ISyncInfoManager, SyncInfoManager>()
            ;
    }

    private ISnapshotManager CreateSnapshotManager([KeyFilter(SnapshotDbName)] IDb db, IBlockTree blockTree, IPenaltyHandler penaltyHandler, IMasternodeVotingContract votingContract, ISpecProvider specProvider)
    {
        return new SnapshotManager(db, blockTree, penaltyHandler, votingContract, specProvider);
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
