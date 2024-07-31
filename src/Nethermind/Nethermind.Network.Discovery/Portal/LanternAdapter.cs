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

public partial class LanternAdapter: ILanternAdapter
{
    private readonly IDiscv5Protocol _discv5;
    private readonly ILogger _logger;
    private readonly ILogManager _logManager;
    private readonly EnrNodeHashProvider _nodeHashProvider = new EnrNodeHashProvider();
    private readonly IIdentityVerifier _identityVerifier;
    private readonly IEnrFactory _enrFactory;
    private readonly ITalkReqTransport _talkReqTransport;
    private readonly IUtpManager _utpManager;

    public LanternAdapter (
        IDiscv5Protocol discv5,
        IIdentityVerifier identityVerifier,
        IEnrFactory enrFactory,
        ITalkReqTransport talkReqTransport,
        IUtpManager utpManager,
        ILogManager logManager
    )
    {
        _discv5 = discv5;
        _identityVerifier = identityVerifier;
        _enrFactory = enrFactory;
        _talkReqTransport = talkReqTransport;
        _utpManager = utpManager;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<LanternAdapter>();
    }

    public IPortalContentNetwork RegisterContentNetwork(byte[] protocol, IPortalContentNetwork.Store store)
    {
        _logger.Warn($"self enode is {_discv5.SelfEnr.NodeId.ToHexString()}");
        var messageSender = new KademliaMessageSender(protocol, _talkReqTransport, _discv5, _enrFactory,
            _identityVerifier, _logManager);

        var kademlia = new Kademlia<IEnr, byte[], LookupContentResult>(
            _nodeHashProvider,
            new PortalContentStoreAdapter(store),
            messageSender,
            _logManager,
            _discv5.SelfEnr,
            20,
            3,
            TimeSpan.FromHours(1)
        );
        _talkReqTransport.RegisterProtocol(protocol, new KademliaTalkReqHandler(kademlia, _discv5.SelfEnr, _utpManager));

        return new PortalContentNetwork(_utpManager, kademlia, messageSender, _logger);
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
