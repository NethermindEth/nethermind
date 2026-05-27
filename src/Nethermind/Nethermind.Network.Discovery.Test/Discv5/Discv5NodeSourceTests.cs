// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class Discv5NodeSourceTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task DiscoverNodes_ShouldNotRetainDroppedNodesInRecentDedupe(CancellationToken token)
    {
        IKademlia<PublicKey, Node> kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
        kademlia.IterateNodes().Returns(Array.Empty<Node>());
        Discv5NodeSource source = new(
            kademlia,
            new KademliaConfig<Node> { CurrentNodeId = CreateNode(0) },
            LimboLogs.Instance);

        await using IAsyncEnumerator<Node> enumerator = source.DiscoverNodes(token).GetAsyncEnumerator(token);
        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        await Task.Yield();
        Node firstNode = CreateNode(1);
        RaiseNode(kademlia, firstNode);

        Assert.That(await firstMove.AsTask(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(firstNode));

        for (int i = 2; i < 66; i++)
        {
            RaiseNode(kademlia, CreateNode(i));
        }

        Node droppedNode = CreateNode(100);
        RaiseNode(kademlia, droppedNode);

        for (int i = 2; i < 66; i++)
        {
            Assert.That(await enumerator.MoveNextAsync(), Is.True);
        }

        ValueTask<bool> droppedMove = enumerator.MoveNextAsync();
        await Task.Yield();
        RaiseNode(kademlia, droppedNode);

        Assert.That(await droppedMove.AsTask(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(droppedNode));
    }

    private static Node CreateNode(int index) =>
        new(TestItem.PublicKeys[index], $"192.168.1.{index + 1}", 30303);

    private static void RaiseNode(IKademlia<PublicKey, Node> kademlia, Node node) =>
        kademlia.OnNodeAdded += Raise.Event<EventHandler<Node>>(null, node);
}
