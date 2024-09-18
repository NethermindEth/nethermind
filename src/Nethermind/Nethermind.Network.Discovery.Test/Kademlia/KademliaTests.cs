// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lantern.Discv5.WireProtocol.Session;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KademliaTests
{
    private readonly IKademlia<ValueHash256, ValueHash256, ValueHash256>.IStore _store = Substitute.For<IKademlia<ValueHash256, ValueHash256, ValueHash256>.IStore>();
    private readonly IMessageSender<ValueHash256, ValueHash256, ValueHash256> _messageSender = Substitute.For<IMessageSender<ValueHash256, ValueHash256, ValueHash256>>();

    private Kademlia<ValueHash256, ValueHash256, ValueHash256> CreateKad(KademliaConfig<ValueHash256> config)
    {
        return new ServiceCollection()
            .ConfigureKademliaComponents<ValueHash256, ValueHash256, ValueHash256>()
            .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Trace))
            .AddSingleton<INodeHashProvider<ValueHash256>>(new ValueHashNodeHashProvider())
            .AddSingleton<IContentHashProvider<ValueHash256>>(new ValueHashNodeHashProvider())
            .AddSingleton(config)
            .AddSingleton(_store)
            .AddSingleton(_messageSender)
            .AddSingleton<Kademlia<ValueHash256, ValueHash256, ValueHash256>>()
            .BuildServiceProvider()
            .GetRequiredService<Kademlia<ValueHash256, ValueHash256, ValueHash256>>();
    }

    [Test]
    public void test()
    {
        var prop = typeof(SessionMain)
            .GetProperty("IsEstablished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.SetProperty);
    }

    [Test]
    public void TestNewNodeAdded()
    {
        Kademlia<ValueHash256, ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            CurrentNodeId = ValueKeccak.Zero,
            KSize = 5,
            Beta = 0,
            RefreshInterval = TimeSpan.FromSeconds(10)
        });

        int nodeAddedTriggered = 0;
        kad.OnNodeAdded += (sender, hash256) => nodeAddedTriggered++;

        ValueHash256 testHash = new ValueHash256("0x1111111111111111111111111111111111111111111111111111111111111111");
        kad.AddOrRefresh(testHash);
        kad.AddOrRefresh(testHash);
        kad.AddOrRefresh(testHash);

        nodeAddedTriggered.Should().Be(1);
    }

    [Test]
    public async Task TestTooManyNode()
    {
        TaskCompletionSource pingSource = new TaskCompletionSource();
        _messageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        Kademlia<ValueHash256, ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            CurrentNodeId = ValueKeccak.Zero,
            KSize = 5,
            Beta = 0,
            RefreshInterval = TimeSpan.FromSeconds(10)
        });

        ValueHash256[] testHashes = Enumerable.Range(0, 10).Select((k) => Hash256XORUtils.GetRandomHashAtDistance( ValueKeccak.Zero, 250) ).ToArray();
        foreach (ValueHash256 valueHash256 in testHashes[..10])
        {
            kad.AddOrRefresh(valueHash256);
        }

        kad.GetAllAtDistance(250).ToHashSet().Should().BeEquivalentTo(testHashes[..5].ToHashSet());

        pingSource.SetCanceled();

        await Task.Delay(100);

        var afterCancelled = (testHashes[1..5].Concat([testHashes[9]])).ToHashSet();
        Assert.That(() => kad.GetAllAtDistance(250).ToHashSet(), Is.EquivalentTo(afterCancelled).After(100));
    }

    [Test]
    public void TestLocalNeighbours()
    {
        TaskCompletionSource pingSource = new TaskCompletionSource();
        _messageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        Kademlia<ValueHash256, ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            CurrentNodeId = ValueKeccak.Zero,
            KSize = 5,
            Beta = 0,
            RefreshInterval = TimeSpan.FromSeconds(10)
        });

        ValueHash256[] testHashes = Enumerable.Range(0, 7).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();
        foreach (ValueHash256 valueHash256 in testHashes)
        {
            kad.AddOrRefresh(valueHash256);
        }

        kad.GetKNeighbour(ValueKeccak.Zero, null).Length.Should().Be(5);

        foreach (ValueHash256 valueHash256 in testHashes)
        {
            kad.GetKNeighbour(valueHash256, null).Length.Should().Be(5);
        }
    }

    [Test]
    public async Task TestTooManyNodeWithAcceleratedLookup()
    {
        _messageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Kademlia<ValueHash256, ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            CurrentNodeId = ValueKeccak.Zero,
            KSize = 5,
            Beta = 1,
            RefreshInterval = TimeSpan.FromSeconds(10)
        });

        ValueHash256[] testHashes = new IEnumerable<ValueHash256>[]
        {
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XORUtils.GetRandomHashAtDistance(new ValueHash256("0x0000000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XORUtils.GetRandomHashAtDistance(new ValueHash256("0x0100000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XORUtils.GetRandomHashAtDistance(new ValueHash256("0x0200000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XORUtils.GetRandomHashAtDistance(new ValueHash256("0x0300000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
        }.SelectMany(it => it).ToArray();

        foreach (ValueHash256 valueHash256 in testHashes[..20])
        {
            kad.AddOrRefresh(valueHash256);
        }

        await Task.Delay(100);
        kad.GetAllAtDistance(248).ToHashSet().Should().BeEquivalentTo(testHashes[..5].ToHashSet());
        kad.GetAllAtDistance(249).ToHashSet().Should().BeEquivalentTo(testHashes[5..10].ToHashSet());
        kad.GetAllAtDistance(250).ToHashSet().Should().BeEquivalentTo(testHashes[10..].ToHashSet());
    }

    private class ValueHashNodeHashProvider: INodeHashProvider<ValueHash256>, IContentHashProvider<ValueHash256>
    {
        public ValueHash256 GetHash(ValueHash256 node)
        {
            return node;
        }
    }
}
