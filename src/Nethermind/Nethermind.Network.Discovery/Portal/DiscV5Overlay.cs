// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
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

    public async Task Start(CancellationToken token)
    {
        await _kademlia.Bootstrap(token);

        Console.Out.WriteLine("Starting lookup");

        var result = await _kademlia.LookupValue(new ContentKey()
        {
             HeaderKey = new ValueHash256("0xd1714277cf77b6b90f6a47eeb2476df27432d6866d2626aa3e61e86a442570f8")
        }, token);

        Console.Out.WriteLine($"got result {result}");
        Console.Out.WriteLine($"got header {result!.Header}");

        await _kademlia.Run(token);
    }

    public void AddSeed(IEnr node)
    {
        _kademlia.SeedNode(node);
    }
}
