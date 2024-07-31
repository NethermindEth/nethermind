// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.WireProtocol;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class PortalContentNetworkFactory(
    IDiscv5Protocol discv5,
    IIdentityVerifier identityVerifier,
    IEnrFactory enrFactory,
    ITalkReqTransport talkReqTransport,
    IUtpManager utpManager,
    ILogManager logManager)
    : IPortalContentNetworkFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<PortalContentNetworkFactory>();
    private readonly EnrNodeHashProvider _nodeHashProvider = new EnrNodeHashProvider();

    public IPortalContentNetwork Create(byte[] protocol, IPortalContentNetwork.Store store)
    {
        _logger.Warn($"self enode is {discv5.SelfEnr.NodeId.ToHexString()}");
        var messageSender = new KademliaMessageSender(protocol, talkReqTransport, discv5, enrFactory,
            identityVerifier, logManager);

        var kademlia = new Kademlia<IEnr, byte[], LookupContentResult>(
            _nodeHashProvider,
            new PortalContentStoreAdapter(store),
            messageSender,
            logManager,
            discv5.SelfEnr,
            20,
            3,
            TimeSpan.FromHours(1)
        );
        talkReqTransport.RegisterProtocol(protocol, new KademliaTalkReqHandler(kademlia, discv5.SelfEnr, utpManager));

        return new PortalContentNetwork(utpManager, kademlia, messageSender, _logger);
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
