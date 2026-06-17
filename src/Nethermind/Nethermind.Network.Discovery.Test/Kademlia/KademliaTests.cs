// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KademliaTests
{
    private readonly IKademliaMessageSender<ValueHash256, ValueHash256> _kademliaMessageSender = Substitute.For<IKademliaMessageSender<ValueHash256, ValueHash256>>();

    private Kademlia<ValueHash256, ValueHash256> CreateKad(KademliaConfig<ValueHash256> config) =>
        new ContainerBuilder()
            .AddModule(new KademliaModule<ValueHash256, ValueHash256>())
            .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Trace))
            .AddSingleton<ITimestamper>(new ManualTimestamper(new System.DateTime(2025, 5, 13, 21, 0, 0, System.DateTimeKind.Utc)))
            .AddSingleton<IKeyOperator<ValueHash256, ValueHash256>>(new ValueHashNodeHashProvider())
            .AddSingleton(config)
            .AddSingleton(_kademliaMessageSender)
            .AddSingleton<Kademlia<ValueHash256, ValueHash256>>()
            .Build()
            .Resolve<Kademlia<ValueHash256, ValueHash256>>();

    [Test]
    public void TestNewNodeAdded()
    {
        Kademlia<ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 0,
        });

        int nodeAddedTriggered = 0;
        kad.OnNodeAdded += (sender, hash256) => nodeAddedTriggered++;

        ValueHash256 testHash = new("0x1111111111111111111111111111111111111111111111111111111111111111");
        kad.AddOrRefresh(testHash);
        kad.AddOrRefresh(testHash);
        kad.AddOrRefresh(testHash);

        Assert.That(nodeAddedTriggered, Is.EqualTo(1));
    }

    [Test]
    public void TestNodeRemoved()
    {
        Kademlia<ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 0,
        });

        int nodeRemovedTriggered = 0;
        ValueHash256 testHash = new("0x1111111111111111111111111111111111111111111111111111111111111111");
        kad.AddOrRefresh(testHash);
        kad.OnNodeRemoved += (sender, hash256) =>
        {
            nodeRemovedTriggered++;
            Assert.That(hash256, Is.EqualTo(testHash));
        };

        kad.Remove(testHash);

        Assert.That(nodeRemovedTriggered, Is.EqualTo(1));
    }

    [Test]
    public async Task TestTooManyNode()
    {
        TaskCompletionSource<bool> pingSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _kademliaMessageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        Kademlia<ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 0,
        });

        ValueHash256[] testHashes = Enumerable.Range(0, 10).Select((k) => Hash256XorUtils.GetRandomHashAtDistance(ValueKeccak.Zero, 250)).ToArray();
        foreach (ValueHash256 valueHash256 in testHashes[..10])
        {
            kad.AddOrRefresh(valueHash256);
        }

        Assert.That(kad.GetAllAtDistance(250).ToHashSet(), Is.EquivalentTo(testHashes[..5].ToHashSet()));

        pingSource.SetCanceled();

        await Task.Delay(100);

        HashSet<ValueHash256> afterCancelled = (testHashes[1..5].Concat([testHashes[9]])).ToHashSet();
        Assert.That(() => kad.GetAllAtDistance(250).ToHashSet(), Is.EquivalentTo(afterCancelled).After(100));
    }

    [Test]
    public void TestGetKNeighbours()
    {
        TaskCompletionSource<bool> pingSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _kademliaMessageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        Kademlia<ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            CurrentNodeId = ValueKeccak.Compute("something"),
            KSize = 5,
            Beta = 0,
        });

        ValueHash256[] testHashes = Enumerable.Range(0, 7).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();
        foreach (ValueHash256 valueHash256 in testHashes)
        {
            kad.AddOrRefresh(valueHash256);
        }

        Assert.That(kad.GetKNeighbour(ValueKeccak.Zero), Has.Length.EqualTo(5));
        Assert.That(kad.GetKNeighbour(kad.CurrentNode), Does.Contain(kad.CurrentNode));
        foreach (ValueHash256 testHash in testHashes)
        {
            // It must return K items exactly, taking from other bucket if necessary.
            Assert.That(kad.GetKNeighbour(testHash), Has.Length.EqualTo(5));

            // It must find the closest one at least.
            Assert.That(kad.GetKNeighbour(testHash), Does.Contain(testHash));

            // It must exclude a node when hash is specified
            Assert.That(kad.GetKNeighbour(testHash, testHash), Has.Length.EqualTo(5));
            Assert.That(kad.GetKNeighbour(testHash, excludeSelf: true), Does.Not.Contain(kad.CurrentNode));
        }
    }

    [Test]
    public async Task TestTooManyNodeWithAcceleratedLookup()
    {
        _kademliaMessageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(true);

        Kademlia<ValueHash256, ValueHash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 1,
        });

        ValueHash256[] testHashes = new IEnumerable<ValueHash256>[]
        {
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XorUtils.GetRandomHashAtDistance(new("0x0000000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XorUtils.GetRandomHashAtDistance(new("0x0100000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XorUtils.GetRandomHashAtDistance(new("0x0200000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                Hash256XorUtils.GetRandomHashAtDistance(new("0x0300000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
        }.SelectMany(it => it).ToArray();

        foreach (ValueHash256 valueHash256 in testHashes[..20])
        {
            kad.AddOrRefresh(valueHash256);
        }

        await Task.Delay(100);
        Assert.That(kad.GetAllAtDistance(248).ToHashSet(), Is.EquivalentTo(testHashes[..5].ToHashSet()));
        Assert.That(kad.GetAllAtDistance(249).ToHashSet(), Is.EquivalentTo(testHashes[5..10].ToHashSet()));
        Assert.That(kad.GetAllAtDistance(250).ToHashSet(), Is.EquivalentTo(testHashes[10..].ToHashSet()));
    }

    private class ValueHashNodeHashProvider : IKeyOperator<ValueHash256, ValueHash256>
    {
        public ValueHash256 GetKey(ValueHash256 node) => node;

        public ValueHash256 GetKeyHash(ValueHash256 key) => key;

        public ValueHash256 CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth) =>
            Hash256XorUtils.GetRandomHashAtDistance(nodePrefix, depth);
    }
}
