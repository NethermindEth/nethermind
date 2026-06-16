// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

public class XdcSubnetModule : XdcModule
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .Add<StartXdcSubnetBlockProducer>()
            .AddSingleton<XdcSubnetBlockProducerFactory>()
            .Bind<IBlockProducerFactory, XdcSubnetBlockProducerFactory>() // overrides the base producer binding; runner stays XdcBlockProducerFactory
            .AddSingleton(new BlockDecoder(new XdcSubnetHeaderDecoder()))
            .AddSingleton<IEpochSwitchManager, SubnetEpochSwitchManager>()
            .AddSingleton<ISubnetMasternodesCalculator, SubnetMasternodesCalculator>()
            .Bind<IMasternodesCalculator, ISubnetMasternodesCalculator>()
            .AddSingleton<ISealValidator, XdcSubnetSealValidator>()
            .Bind<ISnapshotManager, ISubnetSnapshotManager>()
            .AddSingleton<IPenaltyHandler, SubnetPenaltyHandler>();

        builder.RegisterType<SubnetSnapshotManager>().As<ISubnetSnapshotManager>().WithAttributeFiltering().SingleInstance();
    }
}
