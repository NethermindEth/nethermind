// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Network;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
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
            .AddSingleton<IHeaderDecoder, XdcSubnetHeaderDecoder>()
            .AddSingleton(new BlockDecoder(new XdcSubnetHeaderDecoder()))
            .AddSingleton<IZeroMessageSerializer<BlockHeadersMessage>>(new BlockHeadersMessageSerializer(new XdcSubnetHeaderDecoder()))
            .AddSingleton(new SerializerInfo(typeof(BlockHeadersMessage), new BlockHeadersMessageSerializer(new XdcSubnetHeaderDecoder())))
            .AddSingleton<IEpochSwitchManager, SubnetEpochSwitchManager>()
            .AddSingleton<ISubnetMasternodesCalculator, SubnetMasternodesCalculator>()
            .Bind<IMasternodesCalculator, ISubnetMasternodesCalculator>()
            .AddSingleton<ISealValidator, XdcSubnetSealValidator>()
            .Bind<ISnapshotManager, ISubnetSnapshotManager>()
            .AddSingleton<IPenaltyHandler, SubnetPenaltyHandler>();

        builder.RegisterType<SubnetSnapshotManager>().As<ISubnetSnapshotManager>().WithAttributeFiltering().SingleInstance();
    }

    protected override void RegisterRewardCalculatorSource(ContainerBuilder builder) =>
        builder.AddDecorator<IRewardCalculatorSource, XdcSubnetRewardCalculatorSource>();
}
