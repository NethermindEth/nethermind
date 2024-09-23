// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Kademlia.Content;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection ConfigurePortalNetworkCommonServices(this IServiceCollection services)
    {
        return services
            .AddSingleton<ITalkReqTransport, TalkReqTransport>()
            .AddSingleton<IUtpManager, TalkReqUtpManager>();
    }

    public static IServiceCollection ConfigureContentNetwork(this IServiceCollection serviceCollection, IServiceProvider baseServiceProvider)
    {
        return serviceCollection

            // Kademlia integration configurations
            .ConfigureKademliaComponents<IEnr>()
            .ConfigureKademliaContentComponents<IEnr, byte[], LookupContentResult>()
            .AddSingleton<IContentMessageSender<IEnr, byte[], LookupContentResult>, KademliaContentNetworkContentKademliaMessageSender>()
            .AddSingleton<IKademliaMessageSender<IEnr>, KademliaContentNetworkContentKademliaMessageSender>()
            .AddSingleton<IKademliaContentStore<byte[], LookupContentResult>, PortalContentKademliaContentStoreAdapter>()
            .AddSingleton<INodeHashProvider<IEnr>>(EnrNodeHashProvider.Instance)
            .AddSingleton<IContentHashProvider<byte[]>>(ContentKeyHashProvider.Instance)

            // Content network configuration
            .ForwardServiceAsSingleton<IEnrProvider>(baseServiceProvider)
            .ForwardServiceAsSingleton<ITalkReqTransport>(baseServiceProvider)
            .ForwardServiceAsSingleton<IUtpManager>(baseServiceProvider)
            .ForwardServiceAsSingleton<ILogManager>(baseServiceProvider)
            .AddSingleton<ITalkReqProtocolHandler, TalkReqHandler>()
            .AddSingleton<IContentNetworkProtocol, ContentNetworkProtocol>()
            .AddSingleton<IContentDistributor, ContentDistributor>()
            .AddSingleton<ContentLookupService>()
            .AddSingleton<RadiusTracker>()
            .AddSingleton<ContentNetworkRpcHandler>()
            .AddSingleton<IPortalContentNetwork, PortalContentNetwork>();
    }
}
