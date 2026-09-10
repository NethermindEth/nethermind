// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.P2P;

/// <summary>Port of Besu's <c>MessageTrackerTest</c> and <c>UniqueMessageMulticasterTest</c>.</summary>
[Parallelizable(ParallelScope.All)]
public class MessageTrackerTests
{
    [Test]
    public void TracksSeenMessagesUpToTheLimit()
    {
        MessageTracker tracker = new(2);
        tracker.AddSeenMessage([1]);
        tracker.AddSeenMessage([2]);
        Assert.That(tracker.HasSeenMessage([1]), Is.True);
        tracker.AddSeenMessage([3]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.HasSeenMessage([1]), Is.False, "oldest evicted");
            Assert.That(tracker.HasSeenMessage([2]), Is.True);
            Assert.That(tracker.HasSeenMessage([3]), Is.True);
        }
    }

    [Test]
    public void ReAddingASeenMessageDoesNotEvictOthers()
    {
        MessageTracker tracker = new(2);
        tracker.AddSeenMessage([1]);
        tracker.AddSeenMessage([2]);
        tracker.AddSeenMessage([1]);
        Assert.That(tracker.HasSeenMessage([2]), Is.True);
    }

    [Test]
    public void UniqueMulticasterSendsEachMessageOnce()
    {
        IValidatorMulticaster inner = Substitute.For<IValidatorMulticaster>();
        UniqueMessageMulticaster multicaster = new(inner, 10);
        byte[] data = [1, 2, 3];
        multicaster.Send(QbftMessageCode.Prepare, data);
        multicaster.Send(QbftMessageCode.Prepare, data);
        multicaster.Send(QbftMessageCode.Prepare, [4]);
        inner.Received(1).Send(QbftMessageCode.Prepare, data, Arg.Any<IReadOnlyCollection<Address>>());
        inner.Received(2).Send(QbftMessageCode.Prepare, Arg.Any<byte[]>(), Arg.Any<IReadOnlyCollection<Address>>());
    }

    [Test]
    public void GossiperExcludesSenderAndAuthor()
    {
        IValidatorMulticaster inner = Substitute.For<IValidatorMulticaster>();
        QbftGossiper gossiper = new(inner);
        QbftReceivedMessage message = new(QbftMessageCode.Commit, [9], TestItem.AddressA);
        gossiper.Send(message, TestItem.AddressB);
        inner.Received(1).Send(QbftMessageCode.Commit, message.Data, Arg.Is<IReadOnlyCollection<Address>>(static d => d.Count == 2 && d.ContainsAddress(TestItem.AddressA) && d.ContainsAddress(TestItem.AddressB)));
    }
}
