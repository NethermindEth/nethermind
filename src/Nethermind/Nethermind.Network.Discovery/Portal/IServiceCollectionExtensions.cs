// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
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
            .ConfigureKademliaComponents<IEnr, byte[], LookupContentResult>()
            .ForwardServiceAsSingleton<IEnrProvider>(baseServiceProvider)
            .ForwardServiceAsSingleton<ITalkReqTransport>(baseServiceProvider)
            .ForwardServiceAsSingleton<IUtpManager>(baseServiceProvider)
            .ForwardServiceAsSingleton<ILogManager>(baseServiceProvider)
            .AddSingleton<INodeHashProvider<IEnr>>(EnrNodeHashProvider.Instance)
            .AddSingleton<IContentHashProvider<byte[]>>(EnrNodeHashProvider.Instance)
            .AddSingleton<ITalkReqProtocolHandler, TalkReqHandler>()
            .AddSingleton<IContentNetworkProtocol, ContentNetworkProtocol>()
            .AddSingleton<IContentDistributor, ContentDistributor>()
            .AddSingleton<ContentLookupService>()
            .AddSingleton<IMessageSender<IEnr, byte[], LookupContentResult>, KademliaTalkReqMessageSender>()
            .AddSingleton<IKademlia<IEnr, byte[], LookupContentResult>.IStore, PortalContentStoreAdapter>()
            .AddSingleton<IPortalContentNetwork, PortalContentNetwork>();
    }

    private class PortalContentStoreAdapter(IPortalContentNetwork.Store sourceStore) : IKademlia<IEnr, byte[], LookupContentResult>.IStore
    {
        public bool TryGetValue(byte[] contentId, out LookupContentResult? value)
        {
            byte[]? sourceContent = sourceStore.GetContent(contentId);
            if (sourceContent == null)
            {
                value = null;
                return false;
            }

            value = new LookupContentResult()
            {
                Payload = sourceContent
            };
            return true;
        }
    }
}
