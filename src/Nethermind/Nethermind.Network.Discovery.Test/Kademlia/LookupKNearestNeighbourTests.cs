// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
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

    private static (LookupKNearestNeighbour<ValueHash256, ValueHash256, Hash256> Lookup, IRoutingTable<ValueHash256, Hash256> Routing, INodeHealthTracker<ValueHash256> Health) CreateLookup(int alpha, TimeSpan hardTimeout, ValueHash256[] seeds)
    {
        IRoutingTable<ValueHash256, Hash256> routing = Substitute.For<IRoutingTable<ValueHash256, Hash256>>();
        routing.GetKNearestNeighbour(Arg.Any<Hash256>(), Arg.Any<bool>()).Returns(seeds);

        INodeHealthTracker<ValueHash256> health = Substitute.For<INodeHealthTracker<ValueHash256>>();

        LookupKNearestNeighbour<ValueHash256, ValueHash256, Hash256> lookup = new(
            routing,
            IdentityNodeHashProvider.Instance,
            Hash256KademliaDistance.Instance,
            health,
            new KademliaConfig<ValueHash256>
            {
                CurrentNodeId = Self,
                Alpha = alpha,
                KSize = 8,
                LookupFindNeighbourHardTimeout = hardTimeout,
            });

        return (lookup, routing, health);
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_unblock_on_mid_flight_cancellation(int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<ValueHash256, ValueHash256, Hash256> lookup, _, INodeHealthTracker<ValueHash256> health) =
            CreateLookup(alpha, TimeSpan.FromSeconds(30), [Seed1]);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<ValueHash256[]> task = lookup.Lookup(
            IdentityNodeHashProvider.ToHash(Seed1),
            8,
            async (_, t) =>
            {
                await Task.Delay(Timeout.Infinite, t);
                return null;
            },
            cts.Token);

        cts.CancelAfter(100);

        _ = await task;
        health.DidNotReceive().OnRequestFailed(Seed1);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_record_peer_failure_on_find_neighbour_timeout(CancellationToken token)
    {
        (LookupKNearestNeighbour<ValueHash256, ValueHash256, Hash256> lookup, _, INodeHealthTracker<ValueHash256> health) =
            CreateLookup(1, TimeSpan.FromMilliseconds(50), [Seed1]);

        _ = await lookup.Lookup(
            IdentityNodeHashProvider.ToHash(Seed1),
            8,
            async (_, t) =>
            {
                await Task.Delay(Timeout.Infinite, t);
                return null;
            },
            token);

        health.Received().OnRequestFailed(Seed1);
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_return_results_with_different_alpha(int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<ValueHash256, ValueHash256, Hash256> lookup, _, _) =
            CreateLookup(alpha, TimeSpan.FromSeconds(10), [Seed1, Seed2, Seed3]);

        Dictionary<ValueHash256, ValueHash256[]> neighbours = new()
        {
            [Seed1] = [N1],
            [Seed2] = [N2],
            [Seed3] = [],
        };

        ValueHash256[] result = await lookup.Lookup(
            IdentityNodeHashProvider.ToHash(Self),
            8,
            (node, _) => Task.FromResult<ValueHash256[]?>(neighbours.GetValueOrDefault(node, [])),
            token);

        Assert.That(result, Is.Not.Empty);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_drain_cancelled_workers_before_returning(CancellationToken token)
    {
        (LookupKNearestNeighbour<ValueHash256, ValueHash256, Hash256> lookup, _, _) =
            CreateLookup(2, TimeSpan.FromSeconds(10), [Seed1, Seed2, Seed3, N1]);
        TaskCompletionSource cancelledWorkerDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = await lookup.Lookup(
            IdentityNodeHashProvider.ToHash(Self),
            1,
            async (node, findToken) =>
            {
                if (node != Seed1)
                {
                    return [];
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, findToken);
                    return [];
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(100, CancellationToken.None);
                    cancelledWorkerDrained.SetResult();
                    throw;
                }
            },
            token);

        Assert.That(cancelledWorkerDrained.Task.IsCompleted, Is.True);
    }
}
