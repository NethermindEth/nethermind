// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KademliaTests
{
    [Test]
    public async Task TestTooMuch()
    {
        TaskCompletionSource pingSource = new TaskCompletionSource();
        IMessageSender<ValueHash256, ValueHash256, ValueHash256> messageSender = Substitute.For<IMessageSender<ValueHash256, ValueHash256, ValueHash256>>();
        messageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        IKademlia<ValueHash256, ValueHash256, ValueHash256>.IStore store =
            Substitute.For<IKademlia<ValueHash256, ValueHash256, ValueHash256>.IStore>();

        Kademlia<ValueHash256, ValueHash256, ValueHash256> kad = new Kademlia<ValueHash256, ValueHash256, ValueHash256>(
                new ValueHashNodeHashProvider(),
                store,
                messageSender,
                LimboLogs.Instance,
                ValueKeccak.Zero,
                5,
                3,
                TimeSpan.FromSeconds(10)
            );

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) =>
            Hash256XORUtils.GetRandomHashAtDistance(ValueKeccak.Zero, 250)
        ).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            kad.AddOrRefresh(valueHash256);
        }

        kad.GetAllAtDistance(250).ToHashSet().Should().BeEquivalentTo(toAdd[..5].ToHashSet());

        pingSource.SetCanceled();

        await Task.Delay(100);

        var afterCancelled = (toAdd[1..5].Concat([toAdd[9]])).ToHashSet();
        Assert.That(() => kad.GetAllAtDistance(250).ToHashSet(), Is.EquivalentTo(afterCancelled).After(100));
    }


    private class ValueHashNodeHashProvider: INodeHashProvider<ValueHash256, ValueHash256>
    {
        public ValueHash256 GetHash(ValueHash256 node)
        {
            return node;
        }
    }
}
