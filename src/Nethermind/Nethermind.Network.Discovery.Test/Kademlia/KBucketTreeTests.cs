// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Kademlia;
using NUnit.Framework;

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

using System.Net;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTreeTests
{
    private const int SelfHash = 0;

    private static KBucketTree<int, int> CreateTree(
        int k = 4,
        int beta = 0,
        Func<int, int, int>? mergeOnRefresh = null) => new(
        new KademliaConfig<int>
        {
            CurrentNodeId = SelfHash,
            KSize = k,
            Beta = beta,
            MergeOnRefresh = mergeOnRefresh
        },
        IntNodeHashProvider.Instance,
        Int32KademliaDistance.Instance);

    private static void Add(KBucketTree<int, int> tree, int hash) =>
        tree.TryAddOrRefresh(hash, hash, out _);

    [Test]
    public void Health_removal_requires_the_current_active_revision([Values] bool refreshBeforeRemove)
    {
        KBucketTree<int, int> tree = CreateTree();
        Add(tree, 1);
        IHealthRoutingTable<int, int> health = tree;
        Assert.That(health.TryGetActive(1, out int node, out object? revision), Is.True);
        if (refreshBeforeRemove) Add(tree, 1);
        int removed = 0;
        tree.OnNodeRemoved += (_, _) => removed++;
        bool result = health.Remove(1, node, revision!, out RoutingTableNotifications<int> notifications);
        Assert.That(removed, Is.Zero, "Removal must defer subscriber publication.");
        health.Publish(notifications);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(!refreshBeforeRemove));
            Assert.That(tree.TryGet(1, out _), Is.EqualTo(refreshBeforeRemove));
            Assert.That(removed, Is.EqualTo(refreshBeforeRemove ? 0 : 1));
        }
    }

    [Test]
    public void GetAllAtDistance_should_reject_invalid_distance([Values(-1, 33, int.MaxValue)] int distance)
    {
        KBucketTree<int, int> tree = CreateTree();
        Add(tree, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetAllAtDistance(distance));
    }

    [Test]
    public void GetAllAtDistance_should_accept_boundaries([Values(0, 32)] int distance)
    {
        KBucketTree<int, int> tree = CreateTree();
        int node = distance == 0 ? 0 : int.MinValue;
        Add(tree, node);
        Assert.That(tree.GetAllAtDistance(distance), Is.EqualTo(new[] { node }));
    }

    [Test]
    public void Split_should_preserve_lru_order_in_child_buckets()
    {
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: 0);

        int left0 = KeyAtDistance(31, 0x10);
        int left1 = KeyAtDistance(31, 0x11);
        int right0 = KeyAtDistance(30, 0x20);
        int right1 = KeyAtDistance(30, 0x21);

        Add(tree, left0);
        Add(tree, right0);
        Add(tree, left1);
        Add(tree, right1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.GetAllAtDistance(31), Is.EqualTo(new[] { left1, left0 }));
            Assert.That(tree.GetAllAtDistance(30), Is.EqualTo(new[] { right1, right0 }));
        }
    }

    [Test]
    public void GetAllAtDistance_should_include_nodes_in_deeper_split_buckets()
    {
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: 4);

        int deep1 = KeyAtDistance(28, 0x40);
        int deep2 = KeyAtDistance(28, 0x41);
        int deep3 = KeyAtDistance(28, 0x42);

        Add(tree, deep1);
        Add(tree, deep2);
        Add(tree, deep3);

        int[] expectedCandidates = [deep1, deep2, deep3];
        int[] result = tree.GetAllAtDistance(28);
        Assert.That(result, Is.SupersetOf(new[] { deep1, deep2 }));
        Assert.That(result.All(expectedCandidates.Contains), Is.True);
    }

    [Test]
    public void GetOccupancy_should_track_node_count_and_capacity_across_splits()
    {
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: 0);
        Assert.That(tree.GetOccupancy(), Is.EqualTo(new RoutingTableOccupancy(0, 2)));

        Add(tree, KeyAtDistance(31, 0x10));
        Add(tree, KeyAtDistance(31, 0x11));
        Assert.That(tree.GetOccupancy(), Is.EqualTo(new RoutingTableOccupancy(2, 2)));

        // The bucket is full, so admitting a node at another distance splits it and grows the reported capacity.
        Add(tree, KeyAtDistance(30, 0x20));
        Assert.That(tree.GetOccupancy(), Is.EqualTo(new RoutingTableOccupancy(3, 6)));
    }

    [Test]
    public async Task TryAddOrRefresh_should_atomically_merge_concurrent_same_hash_values()
    {
        KBucketTree<int, int> tree = CreateTree(
            k: 1,
            mergeOnRefresh: static (incoming, existing) => Math.Max(incoming, existing));
        using Barrier start = new(3);
        using ManualResetEventSlim higherValueAdded = new();

        Task higherValueAdmission = Task.Run(() =>
        {
            start.SignalAndWait();
            tree.TryAddOrRefresh(1, 2, out _);
            higherValueAdded.Set();
        });
        Task lowerValueAdmission = Task.Run(() =>
        {
            start.SignalAndWait();
            higherValueAdded.Wait();
            tree.TryAddOrRefresh(1, 1, out _);
        });

        start.SignalAndWait();
        await Task.WhenAll(higherValueAdmission, lowerValueAdmission);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(1, out int stored), Is.True);
            Assert.That(stored, Is.EqualTo(2));
        }
    }

    [Test]
    public void TryAddOrRefresh_should_merge_same_hash_values_in_replacement_cache()
    {
        KBucketTree<int, int> tree = CreateTree(
            k: 1,
            mergeOnRefresh: static (incoming, existing) => Math.Max(incoming, existing));
        int activeHash = int.MinValue;
        int replacementHash = int.MinValue + 1;
        tree.TryAddOrRefresh(activeHash, 0, out _);
        tree.TryAddOrRefresh(replacementHash, 2, out _);
        tree.TryAddOrRefresh(replacementHash, 1, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(replacementHash, out int replacement), Is.True);
            Assert.That(replacement, Is.EqualTo(2));
        }

        tree.Remove(activeHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.GetKNearestNeighbour(replacementHash), Is.EqualTo(new[] { 2 }));
            Assert.That(tree.TryGet(activeHash, out _), Is.False);
        }
    }

    [Test]
    public void TryAddOrRefresh_should_reject_mutating_reentry_from_merge()
    {
        KBucketTree<int, int>? tree = null;
        tree = CreateTree(mergeOnRefresh: (incoming, _) =>
        {
            tree!.TryAddOrRefresh(2, 2, out _);
            return incoming;
        });
        tree.TryAddOrRefresh(1, 1, out _);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => tree.TryAddOrRefresh(1, 2, out _))!;

        Assert.That(exception.Message, Does.Contain("must not mutate"));
    }

    [Test]
    public void Table_subnet_quota_should_span_leaves_and_release_after_removal()
    {
        KademliaConfig<int> config = new()
        {
            CurrentNodeId = SelfHash,
            KSize = 2,
            Beta = 0,
            GetSubnet = static _ => 1,
            BucketSubnetLimit = 2,
            TableSubnetLimit = 3
        };
        KBucketTree<int, int> tree = new(config, IntNodeHashProvider.Instance, Int32KademliaDistance.Instance);
        int first = KeyAtDistance(31, 1);
        int second = KeyAtDistance(31, 2);
        int third = KeyAtDistance(30, 3);
        int fourth = KeyAtDistance(30, 4);
        foreach (int node in new[] { first, second, third, fourth }) Add(tree, node);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.GetOccupancy().NodeCount, Is.EqualTo(3));
            Assert.That(tree.TryGet(fourth, out _), Is.False);
        }

        tree.Remove(first);
        Assert.That(tree.TryAddOrRefresh(fourth, fourth, out _), Is.EqualTo(BucketAddResult.Added));
    }

    [Test]
    public void Immediate_promotion_should_emit_one_add_event_and_replacement_removal_no_live_event()
    {
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: 0);
        int first = KeyAtDistance(31, 1);
        int second = KeyAtDistance(31, 2);
        int older = KeyAtDistance(31, 3);
        int newer = KeyAtDistance(31, 4);
        foreach (int node in new[] { first, second, older, newer }) Add(tree, node);
        int additions = 0;
        int removals = 0;
        tree.OnNodeAdded += (_, node) => { Assert.That(node, Is.EqualTo(newer)); additions++; };
        tree.OnNodeRemoved += (_, node) => { Assert.That(node, Is.EqualTo(first)); removals++; };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.Remove(older), Is.True);
            Assert.That(removals, Is.Zero);
            Assert.That(tree.Remove(first), Is.True);
            Assert.That(additions, Is.EqualTo(1));
            Assert.That(removals, Is.EqualTo(1));
        }
    }

    private static int KeyAtDistance(int distance, int suffix)
        => Int32KademliaDistance.Instance.SetBit(suffix, Int32KademliaDistance.Instance.MaxDistance - distance);

    [Test]
    public void Refresh_preserves_configured_identity_role([Values("static", "trusted", "bootnode")] string role)
    {
        KBucketTree<Node, ValueHash256> table = CreateTable();
        Node configured = new(TestItem.PublicKeyB, "8.8.8.1", 30303)
        {
            IsStatic = role == "static",
            IsTrusted = role == "trusted",
            IsBootnode = role == "bootnode"
        };
        table.TryAddOrRefresh(configured.IdHash.ValueHash256, configured, out _);
        Node packet = new(configured.Id, "8.8.8.1", 30303, 30304);
        table.TryAddOrRefresh(packet.IdHash.ValueHash256, packet, out _);
        Assert.That(table.TryGet(packet.IdHash.ValueHash256, out Node? stored), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored!.DiscoveryPort, Is.EqualTo(30304));
            Assert.That(stored.IsStatic, Is.EqualTo(role == "static"));
            Assert.That(stored.IsTrusted, Is.EqualTo(role == "trusted"));
            Assert.That(stored.IsBootnode, Is.EqualTo(role == "bootnode"));
        }
    }

    [Test]
    public void Static_refresh_succeeds_in_full_subnet_leaf()
    {
        KBucketTree<Node, ValueHash256> table = CreateTable();
        Node configured = new(TestItem.PublicKeyB, "8.8.8.1", 30303) { IsStatic = true };
        table.TryAddOrRefresh(configured.IdHash.ValueHash256, configured, out _);
        foreach (Node peer in new[] { new Node(TestItem.PublicKeyC, "8.8.8.2", 30303), new Node(TestItem.PublicKeyD, "8.8.8.3", 30303) })
            Assert.That(table.TryAddOrRefresh(peer.IdHash.ValueHash256, peer, out _), Is.EqualTo(BucketAddResult.Added));
        Node refreshed = new(configured.Id, "8.8.8.1", 30303, 30304);
        table.TryAddOrRefresh(refreshed.IdHash.ValueHash256, refreshed, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(table.TryGet(refreshed.IdHash.ValueHash256, out Node? stored), Is.True);
            Assert.That(stored!.DiscoveryPort, Is.EqualTo(30304));
        }
    }

    [Test]
    public void Bootnodes_share_a_subnet_without_weakening_ordinary_quota([Values] bool bootnodes)
    {
        KBucketTree<Node, ValueHash256> table = CreateTable();
        for (int i = 1; i <= 3; i++)
        {
            Node node = new(TestItem.PrivateKeys[i].PublicKey, $"8.8.8.{i}", 30303) { IsBootnode = bootnodes };
            table.TryAddOrRefresh(node.IdHash.ValueHash256, node, out _);
        }
        Assert.That(table.GetOccupancy().NodeCount, Is.EqualTo(bootnodes ? 3 : 2));
    }

    private static KBucketTree<Node, ValueHash256> CreateTable()
    {
        Node self = new(TestItem.PublicKeyA, "9.9.9.9", 30303);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig { BucketSize = 64 });
        return new(config, new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()), ValueHash256KademliaDistance.Instance);
    }

    [Test]
    public void Subnet_quota_should_bound_admission_and_release_after_removal(
        [Values("8.8.8.", "::ffff:8.8.8.", "2606:4700:4700::")] string prefix,
        [Values] bool bucketLimit)
    {
        Node self = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        config.KSize = 64;
        config.BucketSubnetLimit = bucketLimit ? 2 : 64;
        config.TableSubnetLimit = bucketLimit ? 64 : 10;
        FromKeyNodeHashProvider<PublicKey, Node, ValueHash256> hashes = new(new PublicKeyKeyOperator());
        KBucketTree<Node, ValueHash256> routing = new(config, hashes, ValueHash256KademliaDistance.Instance);
        int limit = bucketLimit ? 2 : 10;
        Node[] peers = new Node[limit + 1];
        for (int i = 0; i < peers.Length; i++)
        {
            peers[i] = new Node(TestItem.PrivateKeys[i + 1].PublicKey, prefix + (i + 1), 30303);
            routing.TryAddOrRefresh(hashes.GetHash(peers[i]), peers[i], out _);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routing.GetOccupancy().NodeCount, Is.EqualTo(limit));
            Assert.That(routing.TryGet(hashes.GetHash(peers[^1]), out _), Is.False);
        }
        Node independent = new(TestItem.PrivateKeys[20].PublicKey, prefix.StartsWith("2606") ? "2606:4700:4701::1" : "9.9.9.9", 30303);
        Assert.That(routing.TryAddOrRefresh(hashes.GetHash(independent), independent, out _), Is.EqualTo(BucketAddResult.Added));
        routing.Remove(hashes.GetHash(peers[0]));
        Assert.That(routing.TryAddOrRefresh(hashes.GetHash(peers[^1]), peers[^1], out _), Is.EqualTo(BucketAddResult.Added));
    }

    [Test]
    public void Mapped_addresses_should_share_quota_and_local_static_nodes_should_be_exempt()
    {
        Node self = new(TestItem.PublicKeyA, "8.8.8.1", 30303);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        Node native = new(TestItem.PublicKeyB, "8.8.8.2", 30303);
        Node mapped = new(TestItem.PublicKeyC, new IPEndPoint(IPAddress.Parse("::ffff:8.8.8.3"), 30303));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.GetSubnet!(native), Is.EqualTo(config.GetSubnet(mapped)));
            Assert.That(config.GetSubnet(self), Is.Null);
            Assert.That(config.GetSubnet(new Node(TestItem.PublicKeyD, "10.1.1.1", 30303)), Is.Null);
        }
        native.IsStatic = true;
        Assert.That(config.GetSubnet(native), Is.Null);
    }
}
