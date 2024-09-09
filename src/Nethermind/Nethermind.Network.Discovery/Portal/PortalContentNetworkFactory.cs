// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class PortalContentNetworkFactory(
    IEnrProvider enrProvider,
    ITalkReqTransport talkReqTransport,
    IUtpManager utpManager,
    ILogManager logManager)
    : IPortalContentNetworkFactory
{
    public IPortalContentNetwork Create(ContentNetworkConfig config, IPortalContentNetwork.Store store)
    {
        IServiceCollection services = new ServiceCollection()
            .AddSingleton(enrProvider)
            .AddSingleton(talkReqTransport)
            .AddSingleton(utpManager)
            .AddSingleton(logManager)
            .AddSingleton(config)
            .AddSingleton(store)
            .AddSingleton<INodeHashProvider<IEnr, byte[]>>(EnrNodeHashProvider.Instance)
            .AddSingleton<ITalkReqProtocolHandler, TalkReqHandler>()
            .AddSingleton<IContentDistributor, ContentDistributor>()
            .AddSingleton<IMessageSender<IEnr, byte[], LookupContentResult>, KademliaTalkReqMessageSender>()
            .AddSingleton(new KademliaConfig<IEnr>()
            {
                CurrentNodeId = enrProvider.SelfEnr,
                KSize = config.K,
                Alpha = config.A,
                RefreshInterval = config.RefreshInterval
            })
            .AddSingleton<IKademlia<IEnr, byte[], LookupContentResult>.IStore, PortalContentStoreAdapter>()
            .AddSingleton<IKademlia<IEnr, byte[], LookupContentResult>, Kademlia<IEnr, byte[], LookupContentResult>>()
            .AddSingleton<IPortalContentNetwork, PortalContentNetwork>();

        return services.BuildServiceProvider().GetRequiredService<IPortalContentNetwork>();
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
