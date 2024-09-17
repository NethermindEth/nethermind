// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.History.Rpc;

namespace Nethermind.Network.Discovery.Portal.History;

public static class IServiceCollectionExtensions
{
    public static IServiceProvider CreateHistoryNetworkServiceProvider(this IServiceProvider baseServiceProvider, IEnr[] historyNetworkBootnodes)
    {
        ContentNetworkConfig config = new ContentNetworkConfig()
        {
            ProtocolId = Bytes.FromHexString("0x500B"),
            BootNodes = historyNetworkBootnodes
        };

        return new ServiceCollection()
            .ConfigureHistoryNetwork(baseServiceProvider)
            .AddSingleton(config)
            .BuildServiceProvider();
    }

    public static IServiceCollection ConfigureHistoryNetwork(this IServiceCollection serviceCollection, IServiceProvider baseServiceProvider)
    {
        return serviceCollection
            .ConfigureContentNetwork(baseServiceProvider)
            .ForwardServiceAsSingleton<IBlockTree>(baseServiceProvider)
            .AddSingleton<IPortalContentNetwork.Store, HistoryNetworkStore>()
            .AddSingleton<IPortalHistoryNetwork, PortalHistoryNetwork>()
            .AddSingleton<IPortalHistoryRpcModule, PortalHistoryRpcModule>()
            .AddSingleton<KademliaConfig<IEnr>>((provider) =>
            {
                IEnrProvider enrProvider = baseServiceProvider.GetRequiredService<IEnrProvider>();
                ContentNetworkConfig config = provider.GetRequiredService<ContentNetworkConfig>();
                return new KademliaConfig<IEnr>()
                {
                    CurrentNodeId = enrProvider.SelfEnr,
                    KSize = config.K,
                    Alpha = config.A,
                    RefreshInterval = config.RefreshInterval
                };
            });
    }
}
