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

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_unblock_on_mid_flight_cancellation(int alpha, CancellationToken token)
    {
        IRoutingTable<ValueHash256> routingTable = Substitute.For<IRoutingTable<ValueHash256>>();
        ValueHash256 seed = new("0x1100000000000000000000000000000000000000000000000000000000000000");
        routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
            .Returns([seed]);

        INodeHealthTracker<ValueHash256> health = Substitute.For<INodeHealthTracker<ValueHash256>>();

        LookupKNearestNeighbour<ValueHash256, ValueHash256> lookup = new(
            routingTable,
            new IdentityNodeHashProvider(),
            health,
            new KademliaConfig<ValueHash256>
            {
                CurrentNodeId = Self,
                Alpha = alpha,
                KSize = 8,
                LookupFindNeighbourHardTimeout = TimeSpan.FromSeconds(30),
            },
            LimboLogs.Instance);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<ValueHash256[]> task = lookup.Lookup(
            seed,
            8,
            async (_, t) =>
            {
                await Task.Delay(Timeout.Infinite, t);
                return null;
            },
            cts.Token);

        cts.CancelAfter(100);

        ValueHash256[] _ = await task;
        health.Received().OnRequestFailed(seed);
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_return_results_with_different_alpha(int alpha, CancellationToken token)
    {
        IRoutingTable<ValueHash256> routingTable = Substitute.For<IRoutingTable<ValueHash256>>();
        ValueHash256[] seeds =
        [
            new("0x1100000000000000000000000000000000000000000000000000000000000000"),
            new("0x2200000000000000000000000000000000000000000000000000000000000000"),
            new("0x3300000000000000000000000000000000000000000000000000000000000000"),
        ];
        routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
            .Returns(seeds);

        Dictionary<ValueHash256, ValueHash256[]> neighbours = new()
        {
            [seeds[0]] = [new("0x4400000000000000000000000000000000000000000000000000000000000000")],
            [seeds[1]] = [new("0x5500000000000000000000000000000000000000000000000000000000000000")],
            [seeds[2]] = [],
        };

        LookupKNearestNeighbour<ValueHash256, ValueHash256> lookup = new(
            routingTable,
            new IdentityNodeHashProvider(),
            Substitute.For<INodeHealthTracker<ValueHash256>>(),
            new KademliaConfig<ValueHash256>
            {
                CurrentNodeId = Self,
                Alpha = alpha,
                KSize = 8,
                LookupFindNeighbourHardTimeout = TimeSpan.FromSeconds(10),
            },
            LimboLogs.Instance);

        ValueHash256[] result = await lookup.Lookup(
            Self,
            8,
            (node, _) => Task.FromResult<ValueHash256[]?>(neighbours.GetValueOrDefault(node, [])),
            token);

        Assert.That(result, Is.Not.Empty);
    }

    private sealed class IdentityNodeHashProvider : INodeHashProvider<ValueHash256>
    {
        public ValueHash256 GetHash(ValueHash256 node) => node;
    }
}
