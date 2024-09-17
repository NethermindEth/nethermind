// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal.History;

public static class ComponentConfiguration
{
    public static IServiceProvider CreateHistoryNetworkServiceProvider(IServiceProvider baseServiceProvider, IEnr[] historyNetworkBootnodes)
    {
        IServiceCollection serviceCollection = new ServiceCollection();
        ConfigureHistoryNetwork(baseServiceProvider, serviceCollection);

        ContentNetworkConfig config = new ContentNetworkConfig()
        {
            ProtocolId = Bytes.FromHexString("0x500B"),
            BootNodes = historyNetworkBootnodes
        };
        serviceCollection.AddSingleton(config);

        return serviceCollection.BuildServiceProvider();
    }

    public static void ConfigureHistoryNetwork(IServiceProvider baseServiceProvider, IServiceCollection serviceCollection)
    {
        Portal.ComponentConfiguration.ConfigureContentNetwork(baseServiceProvider, serviceCollection);
        serviceCollection
            .AddSingleton<HistoryNetworkStore>()
            .AddSingleton<IPortalHistoryNetwork, PortalHistoryNetwork>()
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
