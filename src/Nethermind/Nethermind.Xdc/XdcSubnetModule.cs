// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc;

public class XdcSubnetModule : XdcModule
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .AddSingleton<ISnapshotManager, IDb, IBlockTree, IMasternodeVotingContract, ISpecProvider, IPenaltyHandler>(CreateSnapshotManager)
            .AddSingleton<IPenaltyHandler, SubnetPenaltyHandler>();

    }

    private ISnapshotManager CreateSnapshotManager([KeyFilter(XdcRocksDbConfigFactory.XdcSnapshotDbName)] IDb db, IBlockTree blockTree, IMasternodeVotingContract votingContract, ISpecProvider specProvider, IPenaltyHandler penaltyHandler) =>
        new SubnetSnapshotManager(db, blockTree, votingContract, specProvider, penaltyHandler);
}
