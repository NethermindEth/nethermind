// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class MsgReqManager
{
    private readonly IKademlia<IEnr, byte[]> _kademlia;
    private readonly IEnrDistanceCalculator _distanceCalculator = new IEnrDistanceCalculator();
    private readonly ILogger _logger;
    private readonly byte[] _protocol;

    public MsgReqManager(
        ILanternAdapter lanternAdapter,
        IEnr currentNodeId,
        byte[] protocol,
        ILogManager logManager
    )
    {
        _protocol = protocol;
        _kademlia = new Kademlia<IEnr, byte[]>(
            _distanceCalculator,
            new NoopStore(),
            lanternAdapter.CreateMessageSenderForProtocol(_protocol),
            currentNodeId,
            20,
            3,
            TimeSpan.FromHours(1)
        );

        lanternAdapter.RegisterKademliaOverlay(_protocol, _kademlia);
        _logger = logManager.GetClassLogger<MsgReqManager>();
    }

}
