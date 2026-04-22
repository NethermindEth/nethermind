// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

public class XdcSubnetModule : XdcModule
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .Add<StartXdcSubnetBlockProducer>()
            .AddSingleton(new BlockDecoder(new XdcSubnetHeaderDecoder()))
            .AddSingleton<IEpochSwitchManager, SubnetEpochSwitchManager>()
            .AddSingleton<ISubnetMasternodesCalculator, SubnetMasternodesCalculator>()
            .Bind<IMasternodesCalculator, ISubnetMasternodesCalculator>()
            .AddSingleton<ISealValidator, XdcSubnetSealValidator>()
            .AddSingleton<ISubnetSnapshotManager, IDb, IBlockTree, IMasternodeVotingContract, ISpecProvider, IPenaltyHandler>(CreateSnapshotManager)
            .Bind<ISnapshotManager, ISubnetSnapshotManager>()
            .AddSingleton<IPenaltyHandler, SubnetPenaltyHandler>();

    }

    private ISubnetSnapshotManager CreateSnapshotManager([KeyFilter(XdcRocksDbConfigFactory.XdcSnapshotDbName)] IDb db, IBlockTree blockTree, IMasternodeVotingContract votingContract, ISpecProvider specProvider, IPenaltyHandler penaltyHandler) =>
        new SubnetSnapshotManager(db, blockTree, votingContract, specProvider, penaltyHandler);
}
