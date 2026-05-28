// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class LookupKNearestNeighbourTests
{
    private static readonly ValueHash256 Self = new("0x0000000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 Seed1 = new("0x1100000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 Seed2 = new("0x2200000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 Seed3 = new("0x3300000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 N1 = new("0x4400000000000000000000000000000000000000000000000000000000000000");
    private static readonly ValueHash256 N2 = new("0x5500000000000000000000000000000000000000000000000000000000000000");

    private static (LookupKNearestNeighbour<ValueHash256, ValueHash256> Lookup, IRoutingTable<ValueHash256> Routing, INodeHealthTracker<ValueHash256> Health) CreateLookup(int alpha, TimeSpan hardTimeout, ValueHash256[] seeds)
    {
        IRoutingTable<ValueHash256> routing = Substitute.For<IRoutingTable<ValueHash256>>();
        routing.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>()).Returns(seeds);

        INodeHealthTracker<ValueHash256> health = Substitute.For<INodeHealthTracker<ValueHash256>>();

        LookupKNearestNeighbour<ValueHash256, ValueHash256> lookup = new(
            routing,
            IdentityNodeHashProvider.Instance,
            health,
            new KademliaConfig<ValueHash256>
            {
                CurrentNodeId = Self,
                Alpha = alpha,
                KSize = 8,
                LookupFindNeighbourHardTimeout = hardTimeout,
            },
            LimboLogs.Instance);

        return (lookup, routing, health);
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_unblock_on_mid_flight_cancellation(int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<ValueHash256, ValueHash256> lookup, _, INodeHealthTracker<ValueHash256> health) =
            CreateLookup(alpha, TimeSpan.FromSeconds(30), [Seed1]);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<ValueHash256[]> task = lookup.Lookup(
            Seed1,
            8,
            async (_, t) =>
            {
                await Task.Delay(Timeout.Infinite, t);
                return null;
            },
            cts.Token);

        cts.CancelAfter(100);

        _ = await task;
        health.Received().OnRequestFailed(Seed1);
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_return_results_with_different_alpha(int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<ValueHash256, ValueHash256> lookup, _, _) =
            CreateLookup(alpha, TimeSpan.FromSeconds(10), [Seed1, Seed2, Seed3]);

        Dictionary<ValueHash256, ValueHash256[]> neighbours = new()
        {
            [Seed1] = [N1],
            [Seed2] = [N2],
            [Seed3] = [],
        };

        ValueHash256[] result = await lookup.Lookup(
            Self,
            8,
            (node, _) => Task.FromResult<ValueHash256[]?>(neighbours.GetValueOrDefault(node, [])),
            token);

        Assert.That(result, Is.Not.Empty);
    }
}
