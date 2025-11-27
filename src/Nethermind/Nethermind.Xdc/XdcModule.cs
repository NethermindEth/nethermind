// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public class XdcModule : Module
{
    private const string SnapshotDbName = "Snapshots";

    protected override void Load(ContainerBuilder builder)
    {
        builder.AddStep(typeof(XdcInitNetwork));

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

            // sealer
            .AddSingleton<ISealer, XdcSealer>()

            // penalty handler

            // reward handler

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
            .AddSingleton<ISnapshotManager, IDb, IBlockTree, IPenaltyHandler>(CreateSnapshotManager)
            .AddSingleton<IPenaltyHandler, PenaltyHandler>()
            .AddSingleton<ITimeoutTimer, TimeoutTimer>()
            .AddSingleton<ISyncInfoManager, SyncInfoManager>()
            ;
    }

    private ISnapshotManager CreateSnapshotManager([KeyFilter(SnapshotDbName)] IDb db, IBlockTree blockTree, IPenaltyHandler penaltyHandler)
    {
        return new SnapshotManager(db, blockTree, penaltyHandler);
    }

}
