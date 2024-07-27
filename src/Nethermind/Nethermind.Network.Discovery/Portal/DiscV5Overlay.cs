// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class DiscV5Overlay
{
    private readonly IKademlia<IEnr, ContentKey, ContentContent> _kademlia;
    private readonly EnrNodeHashProvider _nodeHashProvider = new EnrNodeHashProvider();
    private readonly ILogger _logger;
    private readonly byte[] _protocol;

    public DiscV5Overlay(
        ILanternAdapter lanternAdapter,
        IEnr currentNodeId,
        byte[] protocol,
        ILogManager logManager
    )
    {
        _protocol = protocol;
        _kademlia = new Kademlia<IEnr, ContentKey, ContentContent>(
            _nodeHashProvider,
            new NoopStore(),
            lanternAdapter.CreateMessageSenderForProtocol(_protocol),
            logManager,
            currentNodeId,
            20,
            3,
            TimeSpan.FromHours(1)
        );

        lanternAdapter.RegisterKademliaOverlay(_protocol, _kademlia);
        _logger = logManager.GetClassLogger<DiscV5Overlay>();
    }

    public Task Start(CancellationToken token)
    {
        return _kademlia.Run(token);
    }

    public void AddSeed(IEnr node)
    {
        _kademlia.SeedNode(node);
    }
}
