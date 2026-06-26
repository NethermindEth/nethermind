// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class KademliaTests
{
    [Test]
    public void Routing_table_returns_nodes_closest_to_target()
    {
        TorrentKademlia kademlia = new(new KadId(CreateId(0x00)), k: 8);
        DhtNode far = new(new KadId(CreateId(0xf0)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1));
        DhtNode near = new(new KadId(CreateId(0x01)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2));
        DhtNode middle = new(new KadId(CreateId(0x10)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 3));
        kademlia.AddOrRefresh(far);
        kademlia.AddOrRefresh(near);
        kademlia.AddOrRefresh(middle);

        List<DhtNode> closest = kademlia.GetClosest(new KadId(CreateId(0x00)), 3);

        Assert.That(closest[0], Is.EqualTo(near));
        Assert.That(closest[1], Is.EqualTo(middle));
        Assert.That(closest[2], Is.EqualTo(far));
    }

    [Test]
    public async Task LookupAsync_uses_nethermind_lookup_algorithm()
    {
        TorrentKademlia kademlia = new(new KadId(CreateId(0x00)), k: 4, alpha: 1);
        DhtNode seed = new(new KadId(CreateId(0x40)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1));
        DhtNode neighbour = new(new KadId(CreateId(0x01)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2));
        kademlia.AddOrRefresh(seed);

        List<DhtNode> result = await kademlia.LookupAsync(
            new KadId(CreateId(0x00)),
            (node, _) =>
            {
                IReadOnlyList<DhtNode> neighbours = node.Equals(seed) ? [neighbour] : [];
                return Task.FromResult<IReadOnlyList<DhtNode>?>(neighbours);
            },
            TestContext.CurrentContext.CancellationToken);

        Assert.That(result, Does.Contain(seed));
    }

    [Test]
    public void ToValueHash_zero_pads_dht_ids_deterministically()
    {
        KadId id = new(CreateId(0x42));

        byte[] first = DhtKeyOperator.ToValueHash(id).Bytes.ToArray();
        byte[] second = DhtKeyOperator.ToValueHash(id).Bytes.ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first.AsSpan(KadId.Length).ToArray(), Is.EqualTo(new byte[first.Length - KadId.Length]));
        }
    }

    [Test]
    public async Task LookupAsync_limits_fresh_candidates_returned_to_nethermind_lookup()
    {
        TorrentKademlia kademlia = new(new KadId(CreateId(0x00)), k: 4, alpha: 1);
        DhtNode seed = new(new KadId(CreateId(0x40)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1));
        kademlia.AddOrRefresh(seed);
        int queryCount = 0;

        _ = await kademlia.LookupAsync(
            new KadId(CreateId(0x00)),
            (node, _) =>
            {
                queryCount++;
                IReadOnlyList<DhtNode> neighbours = node.Equals(seed) ? CreateNodes(20) : [];
                return Task.FromResult<IReadOnlyList<DhtNode>?>(neighbours);
            },
            TestContext.CurrentContext.CancellationToken,
            maxFreshCandidates: 4);

        Assert.That(queryCount, Is.EqualTo(5));
    }

    [Test]
    public async Task LookupAsync_does_not_spend_candidate_budget_on_duplicate_or_known_nodes()
    {
        TorrentKademlia kademlia = new(new KadId(CreateId(0x00)), k: 4, alpha: 1);
        DhtNode seed = new(new KadId(CreateId(0x40)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1));
        DhtNode uniqueA = new(new KadId(CreateId(0x02)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2));
        DhtNode uniqueB = new(new KadId(CreateId(0x03)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 3));
        DhtNode uniqueC = new(new KadId(CreateId(0x04)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 4));
        kademlia.AddOrRefresh(seed);
        int queryCount = 0;

        _ = await kademlia.LookupAsync(
            new KadId(CreateId(0x00)),
            (node, _) =>
            {
                queryCount++;
                IReadOnlyList<DhtNode> neighbours = node.Equals(seed)
                    ? [seed, seed, uniqueA, uniqueB, uniqueC]
                    : [];
                return Task.FromResult<IReadOnlyList<DhtNode>?>(neighbours);
            },
            TestContext.CurrentContext.CancellationToken,
            maxFreshCandidates: 2);

        Assert.That(queryCount, Is.EqualTo(3));
    }

    [Test]
    public async Task LookupAsync_returns_known_nodes_without_spending_fresh_candidate_budget()
    {
        TorrentKademlia kademlia = new(new KadId(CreateId(0x00)), k: 1, alpha: 1);
        DhtNode seed = new(new KadId(CreateId(0x01)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1));
        DhtNode known = new(new KadId(CreateId(0x80)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2));
        kademlia.AddOrRefresh(seed);
        kademlia.AddOrRefresh(known);
        int queryCount = 0;

        _ = await kademlia.LookupAsync(
            new KadId(CreateId(0x00)),
            (node, _) =>
            {
                queryCount++;
                IReadOnlyList<DhtNode> neighbours = node.Equals(seed) ? [known] : [];
                return Task.FromResult<IReadOnlyList<DhtNode>?>(neighbours);
            },
            TestContext.CurrentContext.CancellationToken,
            maxFreshCandidates: 0);

        Assert.That(queryCount, Is.EqualTo(2));
    }

    [Test]
    public async Task LookupAsync_removes_failed_query_node_through_nethermind_health_tracker()
    {
        TorrentKademlia kademlia = new(new KadId(CreateId(0x00)), k: 4, alpha: 1);
        DhtNode seed = new(new KadId(CreateId(0x40)), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1));
        kademlia.AddOrRefresh(seed);

        _ = await kademlia.LookupAsync(
            new KadId(CreateId(0x00)),
            (_, _) => throw new TimeoutException(),
            TestContext.CurrentContext.CancellationToken);

        Assert.That(kademlia.GetClosest(new KadId(CreateId(0x00)), 1), Is.Empty);
    }

    private static DhtNode[] CreateNodes(int count)
    {
        DhtNode[] nodes = new DhtNode[count];
        for (int i = 0; i < nodes.Length; i++)
        {
            byte first = checked((byte)(i + 2));
            nodes[i] = new DhtNode(
                new KadId(CreateId(first)),
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, i + 2));
        }

        return nodes;
    }

    private static byte[] CreateId(byte first)
    {
        byte[] bytes = new byte[KadId.Length];
        bytes[0] = first;
        return bytes;
    }
}
