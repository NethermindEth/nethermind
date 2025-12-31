// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
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
using Nethermind.Init.Modules;
using Nethermind.Network;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Contracts;
using Nethermind.Abi;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Logging;

namespace Nethermind.Xdc;

public class XdcModule : Module
{
    private const string SnapshotDbName = "Snapshots";

    protected override void Load(ContainerBuilder builder)
    {
        builder.AddStep(typeof(XdcInitializeNetwork));

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
            .AddSingleton<IRewardCalculator, XdcRewardCalculator>()
            .AddSingleton<IMasternodeVotingContract, ISpecProvider, IAbiEncoder, IWorldStateManager, IReadOnlyTxProcessingEnvFactory, ILogManager>(CreateMasternodeVotingContract)

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
            .AddSingleton<ISnapshotManager, IDb, IBlockTree, IPenaltyHandler>(CreateSnapshotManager)
            .AddSingleton<IPenaltyHandler, PenaltyHandler>()
            .AddSingleton<ITimeoutTimer, TimeoutTimer>()
            .AddSingleton<ISyncInfoManager, SyncInfoManager>()

            //Network
            .AddSingleton<IProtocolValidator, XdcProtocolValidator>()
            .AddSingleton<IHeaderDecoder, XdcHeaderDecoder>()
            .AddSingleton(new BlockDecoder(new XdcHeaderDecoder()))
            ;
    }

    private ISnapshotManager CreateSnapshotManager([KeyFilter(SnapshotDbName)] IDb db, IBlockTree blockTree, IPenaltyHandler penaltyHandler)
    {
        return new SnapshotManager(db, blockTree, penaltyHandler);
    }

    private IMasternodeVotingContract CreateMasternodeVotingContract(
        ISpecProvider specProvider,
        IAbiEncoder abiEncoder,
        IWorldStateManager worldStateManager,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
        ILogManager logManager)
    {
        var xdcSpec = specProvider.GenesisSpec as IXdcReleaseSpec;
        IWorldStateScopeProvider scopeProvider = worldStateManager.CreateResettableWorldState();
        IWorldState worldState = new WorldState(scopeProvider, logManager);
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource = readOnlyTxProcessingEnvFactory.Create();
        return new MasternodeVotingContract(worldState, abiEncoder, xdcSpec.MasternodeVotingContract, readOnlyTxProcessorSource);
    }

}
