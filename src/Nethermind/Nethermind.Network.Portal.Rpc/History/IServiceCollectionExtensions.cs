// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Network.Kademlia;
using Nethermind.Network.Portal.History.Rpc;

namespace Nethermind.Network.Portal.History;

public static class IServiceCollectionExtensions
{
    public static IServiceProvider CreateHistoryNetworkServiceProviderWithRpc(this IServiceProvider baseServiceProvider, IEnr[] historyNetworkBootnodes)
    {
        var config = new ContentNetworkConfig()
        {
            ProtocolId = Bytes.FromHexString("0x500B"),
            BootNodes = historyNetworkBootnodes
        };

        return new ServiceCollection()
            .ConfigureHistoryNetworkWithRpc(baseServiceProvider)
            .AddSingleton(config)
            .BuildServiceProvider();
    }

    public static IServiceCollection ConfigureHistoryNetworkWithRpc(this IServiceCollection serviceCollection, IServiceProvider baseServiceProvider)
    {
        return serviceCollection
            .ConfigureContentNetworkWithRpc(baseServiceProvider)
            .ForwardServiceAsSingleton<IBlockTree>(baseServiceProvider)
            .ForwardServiceAsSingleton<IReceiptStorage>(baseServiceProvider)
            .ForwardServiceAsSingleton<IReceiptFinder>(baseServiceProvider)
            .AddSingleton<IPortalContentNetworkStore, HistoryNetworkStore>()
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
