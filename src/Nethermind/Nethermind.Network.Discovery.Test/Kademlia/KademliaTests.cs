// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KademliaTests
{
    private readonly IKademliaMessageSender<ValueHash256, ValueHash256> _kademliaMessageSender = Substitute.For<IKademliaMessageSender<ValueHash256, ValueHash256>>();

    private Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> CreateKad(KademliaConfig<ValueHash256> config) =>
        new ContainerBuilder()
            .AddModule(new KademliaModule<ValueHash256, ValueHash256, Hash256>())
            .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Trace))
            .AddSingleton<ITimestamper>(new ManualTimestamper(new System.DateTime(2025, 5, 13, 21, 0, 0, System.DateTimeKind.Utc)))
            .AddSingleton<IKademliaDistance<Hash256>>(Hash256KademliaDistance.Instance)
            .AddSingleton<IKeyOperator<ValueHash256, ValueHash256, Hash256>>(new ValueHashKeyOperator<ValueHash256>(static node => node))
            .AddSingleton(config)
            .AddSingleton(_kademliaMessageSender)
            .AddSingleton<Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256>>()
            .Build()
            .Resolve<Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256>>();

    [Test]
    public void TestNewNodeAdded()
    {
        Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad = CreateKad(new KademliaConfig<ValueHash256>
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
        Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad = CreateKad(new KademliaConfig<ValueHash256>
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
    public void ShouldSeedBootnodes()
    {
        ValueHash256 bootNode = ValueKeccak.Compute("bootnode");
        Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 0,
            BootNodes = [bootNode],
        });

        Assert.That(kad.IterateNodes(), Does.Contain(bootNode));
    }

    [Test]
    public async Task TestTooManyNode()
    {
        TaskCompletionSource<bool> pingSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _kademliaMessageSender
            .Ping(Arg.Any<ValueHash256>(), Arg.Any<CancellationToken>())
            .Returns(pingSource.Task);

        Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 0,
        });

        ValueHash256[] testHashes = Enumerable.Range(0, 10).Select((k) => RandomValueHashAtDistance(ValueKeccak.Zero, 250)).ToArray();
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

        Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad = CreateKad(new KademliaConfig<ValueHash256>
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

        Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 1,
        });

        ValueHash256[] testHashes = new IEnumerable<ValueHash256>[]
        {
            Enumerable.Range(0, 5).Select((k) =>
                RandomValueHashAtDistance(new("0x0000000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                RandomValueHashAtDistance(new("0x0100000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                RandomValueHashAtDistance(new("0x0200000000000000000000000000000000000000000000000000000000000000"), 248)
            ),
            Enumerable.Range(0, 5).Select((k) =>
                RandomValueHashAtDistance(new("0x0300000000000000000000000000000000000000000000000000000000000000"), 248)
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

    [Test]
    public void PruneLastBucketRefreshTicks_removes_stale_prefixes_even_when_counts_match()
    {
        Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad = CreateKad(new KademliaConfig<ValueHash256>
        {
            KSize = 5,
            Beta = 0,
        });

        Hash256 activePrefix = new("0x1111111111111111111111111111111111111111111111111111111111111111");
        Hash256 stalePrefix = new("0x2222222222222222222222222222222222222222222222222222222222222222");
        Dictionary<Hash256, long> lastRefreshTicks = GetLastBucketRefreshTicks(kad);
        lastRefreshTicks[activePrefix] = 1;
        lastRefreshTicks[stalePrefix] = 2;

        using PooledSet<Hash256> activePrefixes = new(2);
        activePrefixes.Add(activePrefix);
        activePrefixes.Add(new("0x3333333333333333333333333333333333333333333333333333333333333333"));

        typeof(Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256>)
            .GetMethod("PruneLastBucketRefreshTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(kad, [activePrefixes]);

        Assert.That(lastRefreshTicks.ContainsKey(activePrefix), Is.True);
        Assert.That(lastRefreshTicks.ContainsKey(stalePrefix), Is.False);
    }

    private static Hash256 ToHash(ValueHash256 hash) => ValueHashKeyOperator<ValueHash256>.ToHash(hash);

    private static ValueHash256 ToValueHash(Hash256 hash) => ValueHashKeyOperator<ValueHash256>.ToValueHash(hash);

    private static ValueHash256 RandomValueHashAtDistance(ValueHash256 currentHash, int distance) =>
        ToValueHash(Hash256KademliaDistance.Instance.GetRandomHashAtDistance(ToHash(currentHash), distance));

    private static Dictionary<Hash256, long> GetLastBucketRefreshTicks(Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256> kad)
        => (Dictionary<Hash256, long>)typeof(Nethermind.Kademlia.Kademlia<ValueHash256, ValueHash256, Hash256>)
            .GetField("_lastBucketRefreshTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kad)!;
}
