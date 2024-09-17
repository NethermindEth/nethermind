// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class ComponentConfiguration
{
    public static void ConfigureCommonServices(IServiceCollection services)
    {
        services.AddSingleton<ITalkReqTransport, TalkReqTransport>();
        services.AddSingleton<IUtpManager, TalkReqUtpManager>();
    }

    public static void ConfigureContentNetwork(IServiceProvider serviceProvider, IServiceCollection serviceCollection)
    {
        serviceCollection
            .ForwardServiceAsSingleton<IEnrProvider>(serviceProvider)
            .ForwardServiceAsSingleton<ITalkReqTransport>(serviceProvider)
            .ForwardServiceAsSingleton<IUtpManager>(serviceProvider)
            .ForwardServiceAsSingleton<ILogManager>(serviceProvider)
            .AddSingleton<INodeHashProvider<IEnr, byte[]>>(EnrNodeHashProvider.Instance)
            .AddSingleton<ITalkReqProtocolHandler, TalkReqHandler>()
            .AddSingleton<IContentNetworkProtocol, ContentNetworkProtocol>()
            .AddSingleton<IContentDistributor, ContentDistributor>()
            .AddSingleton<ContentLookupService>()
            .AddSingleton<IMessageSender<IEnr, byte[], LookupContentResult>, KademliaTalkReqMessageSender>()
            .AddSingleton<IKademlia<IEnr, byte[], LookupContentResult>.IStore, PortalContentStoreAdapter>()
            .AddSingleton<IKademlia<IEnr, byte[], LookupContentResult>, Kademlia<IEnr, byte[], LookupContentResult>>()
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
