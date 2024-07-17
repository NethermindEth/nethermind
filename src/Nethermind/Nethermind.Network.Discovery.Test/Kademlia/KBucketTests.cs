// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTests
{
    [Test]
    public void TestKBucketAdd()
    {
        KBucket<ValueHash256, ValueHash256> bucket = new(5, Substitute.For<IMessageSender<ValueHash256, ValueHash256>>());

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.AddOrRefresh(valueHash256);
        }

        // Again
        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.AddOrRefresh(valueHash256);
        }

        bucket.GetAll().ToHashSet().Should().BeEquivalentTo(toAdd[..5].ToHashSet());
    }

    [Test]
    public async Task WhenFull_OnPingTimeout_UseReplacementCache()
    {
        TaskCompletionSource pingSource = new TaskCompletionSource();
        IMessageSender<ValueHash256, ValueHash256> messageSender = Substitute.For<IMessageSender<ValueHash256, ValueHash256>>();
        messageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        KBucket<ValueHash256, ValueHash256> bucket = new(5, messageSender);

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();
        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.AddOrRefresh(valueHash256);
        }

        bucket.GetAll().ToHashSet().Should().BeEquivalentTo(toAdd[..5].ToHashSet());

        pingSource.SetCanceled();

        await Task.Delay(100);

        var afterCancelled = (toAdd[1..5].Concat([toAdd[9]])).ToHashSet();
        Assert.That(() => bucket.GetAll().ToHashSet(), Is.EquivalentTo(afterCancelled).After(100));
    }
}
