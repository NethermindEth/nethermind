// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.StateMachine;

/// <summary>Port of Besu's <c>FutureMessageBufferTest</c>.</summary>
[Parallelizable(ParallelScope.All)]
public class FutureMessageBufferTests
{
    private static QbftReceivedMessage Message(int size = 4) => new(QbftMessageCode.Prepare, new byte[size], TestItem.AddressA);

    [Test]
    public void MessagesForFutureHeightsAreBufferedAndRetrieved()
    {
        FutureMessageBuffer buffer = new(10, 10, 1);
        QbftReceivedMessage message = Message();
        buffer.AddMessage(2, message);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TotalMessageCount, Is.EqualTo(1));
            Assert.That(buffer.RetrieveMessagesForHeight(2), Is.EqualTo(new[] { message }));
            Assert.That(buffer.TotalMessageCount, Is.EqualTo(0));
        }
    }

    [Test]
    public void MessagesAtOrBelowChainHeightAreDropped()
    {
        FutureMessageBuffer buffer = new(10, 10, 5);
        buffer.AddMessage(5, Message());
        buffer.AddMessage(4, Message());
        Assert.That(buffer.TotalMessageCount, Is.EqualTo(0));
    }

    [Test]
    public void MessagesBeyondMaxDistanceAreDropped()
    {
        FutureMessageBuffer buffer = new(2, 10, 1);
        buffer.AddMessage(3, Message());
        buffer.AddMessage(4, Message());
        Assert.That(buffer.TotalMessageCount, Is.EqualTo(1));
    }

    [Test]
    public void FurthestMessagesAreEvictedWhenTheLimitIsReached()
    {
        FutureMessageBuffer buffer = new(10, 2, 1);
        QbftReceivedMessage near = Message();
        buffer.AddMessage(2, near);
        buffer.AddMessage(5, Message());
        buffer.AddMessage(3, Message());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TotalMessageCount, Is.EqualTo(2));
            Assert.That(buffer.RetrieveMessagesForHeight(2), Is.EqualTo(new[] { near }));
            Assert.That(buffer.RetrieveMessagesForHeight(3), Has.Count.EqualTo(1));
            Assert.That(buffer.RetrieveMessagesForHeight(5), Is.Empty, "the furthest height was evicted");
        }
    }

    [Test]
    public void ZeroLimitDisablesBuffering()
    {
        FutureMessageBuffer buffer = new(10, 0, 1);
        buffer.AddMessage(2, Message());
        Assert.That(buffer.TotalMessageCount, Is.EqualTo(0));
    }

    [Test]
    public void RetrievingAHeightDiscardsEverythingBelowIt()
    {
        FutureMessageBuffer buffer = new(10, 10, 1);
        buffer.AddMessage(2, Message());
        buffer.AddMessage(3, Message());
        QbftReceivedMessage later = Message();
        buffer.AddMessage(4, later);
        List<QbftReceivedMessage> forThree = buffer.RetrieveMessagesForHeight(3);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forThree, Has.Count.EqualTo(1));
            Assert.That(buffer.TotalMessageCount, Is.EqualTo(1));
            Assert.That(buffer.RetrieveMessagesForHeight(4), Is.EqualTo(new[] { later }));
        }
    }

    [Test]
    public void ByteBudgetEvictsLikeTheCountLimit()
    {
        FutureMessageBuffer buffer = new(10, 100, 1, maxTotalMessageBytes: 10);
        buffer.AddMessage(2, Message(6));
        buffer.AddMessage(3, Message(6));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.TotalMessageCount, Is.EqualTo(1));
            Assert.That(buffer.TotalMessageBytes, Is.EqualTo(6));
        }
    }
}
