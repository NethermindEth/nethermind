// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

[TestFixture(true)]
[TestFixture(false)]
public class KademliaTests
{
    private readonly IMessageSender<ValueHash256> _messageSender = Substitute.For<IMessageSender<ValueHash256>>();
    private readonly bool _useTreeBasedBucket;

    public KademliaTests(bool useTreeBasedBucket)
    {
        _useTreeBasedBucket = useTreeBasedBucket;
    }

    private Kademlia<ValueHash256> CreateKad(KademliaConfig<ValueHash256> config)
    {
        config.UseTreeBasedRoutingTable = _useTreeBasedBucket;

        return new ServiceCollection()
            .ConfigureKademliaComponents<ValueHash256>()
            .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Trace))
            .AddSingleton<INodeHashProvider<ValueHash256>>(new ValueHashNodeHashProvider())
            .AddSingleton<IContentHashProvider<ValueHash256>>(new ValueHashNodeHashProvider())
            .AddSingleton(config)
            .AddSingleton(_messageSender)
            .AddSingleton<Kademlia<ValueHash256>>()
            .BuildServiceProvider()
            .GetRequiredService<Kademlia<ValueHash256>>();
    }

    [Test]
    public void TestNewNodeAdded()
    {
        Kademlia<ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            KSize = 5,
            Beta = 0,
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

        Kademlia<ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            KSize = 5,
            Beta = 0,
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
    public void TestGetKNeighbours()
    {
        TaskCompletionSource pingSource = new TaskCompletionSource();
        _messageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        Kademlia<ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            KSize = 5,
            Beta = 0,
        });

        ValueHash256[] testHashes = Enumerable.Range(0, 7).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();
        foreach (ValueHash256 valueHash256 in testHashes)
        {
            kad.AddOrRefresh(valueHash256);
        }

        kad.GetKNeighbour(ValueKeccak.Zero, null).Length.Should().Be(5);
        foreach (ValueHash256 testHash in testHashes)
        {
            // It must return K items exactly, taking from other bucket if necessary.
            kad.GetKNeighbour(testHash, null).Length.Should().Be(5);

            // It must find the closest one at least.
            kad.GetKNeighbour(testHash, null).Should().Contain(testHash);

            // It must exclude a node when hash is specified
            kad.GetKNeighbour(testHash, testHash).Length.Should().Be(5);
            kad.GetKNeighbour(testHash, testHash).Should().NotContain(testHash);
        }
    }

    [Test]
    public async Task TestTooManyNodeWithAcceleratedLookup()
    {
        if (!_useTreeBasedBucket)
        {
            // Accelerated lookup only supported with tree based bucket
            return;
        }

        _messageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Kademlia<ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>()
        {
            KSize = 5,
            Beta = 1,
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
