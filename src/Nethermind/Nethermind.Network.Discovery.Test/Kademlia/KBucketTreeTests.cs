// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

#pragma warning disable NUnit2045 // Assertions are interleaved with routing-table mutations; a failure must stop the sequence.

public class KBucketTreeTests
{
    private const int SelfHash = 0;

    private static KBucketTree<int, int> CreateTree(
        int k = 4,
        int beta = 0,
        Func<int, int, int>? mergeOnRefresh = null,
        IRoutingTableNodeLimiter<int>? limiter = null) => new(
        new KademliaConfig<int>
        {
            CurrentNodeId = SelfHash,
            KSize = k,
            Beta = beta,
            MergeOnRefresh = mergeOnRefresh,
            NodeLimiter = limiter
        },
        IntNodeHashProvider.Instance,
        Int32KademliaDistance.Instance);

    private static BucketAddResult Add(KBucketTree<int, int> tree, int hash) =>
        tree.TryAddOrRefresh(hash, hash, out _);

    private static BucketAddResult Add(KBucketTree<int, int> tree, int hash, int node) =>
        tree.TryAddOrRefresh(hash, node, out _);

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

    private static int KeyAtDistance(int distance, int suffix)
        => Int32KademliaDistance.Instance.SetBit(suffix, Int32KademliaDistance.Instance.MaxDistance - distance);

    private static (KBucketTree<int, int> Tree, SubnetLimiter Limiter) CreateSaturatedTree(int k = 16)
    {
        SubnetLimiter limiter = new(bucketLimit: 2, tableLimit: 10);
        KBucketTree<int, int> tree = CreateTree(k: k, limiter: limiter);
        Add(tree, 1000, 100);
        Add(tree, 1001, 101);
        return (tree, limiter);
    }

    private const int HighBit = unchecked((int)0x80000000);
    private const int SecondBit = 0x40000000;

    [Test]
    public void Third_same_subnet_node_in_one_bucket_is_rejected_and_the_first_two_are_kept()
    {
        (KBucketTree<int, int> tree, SubnetLimiter limiter) = CreateSaturatedTree();

        Assert.That(Add(tree, 1002, 102), Is.EqualTo(BucketAddResult.Rejected));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(1000, out _), Is.True);
            Assert.That(tree.TryGet(1001, out _), Is.True);
            Assert.That(tree.TryGet(1002, out _), Is.False);
            Assert.That(limiter.CountFor(1), Is.EqualTo(2));
        }
    }

    [TestCase(2, false)]
    [TestCase(3, true)]
    public void Same_subnet_node_in_a_different_bucket_is_admitted_up_to_the_table_cap(
        int tableLimit,
        bool expectedAdmitted)
    {
        SubnetLimiter limiter = new(bucketLimit: 2, tableLimit: tableLimit);
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: 32, limiter: limiter);

        Add(tree, SecondBit, 100);
        Add(tree, SecondBit | 1, 101);
        // A second subnet triggers the split that puts the following node in another bucket.
        Add(tree, HighBit, 200);

        BucketAddResult result = Add(tree, HighBit | 1, 102);
        Assert.That(result, Is.EqualTo(expectedAdmitted ? BucketAddResult.Added : BucketAddResult.Rejected));
    }

    [Test]
    public void Same_subnet_node_landing_in_empty_child_after_split_is_admitted()
    {
        SubnetLimiter limiter = new(bucketLimit: 2, tableLimit: 10);
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: 32, limiter: limiter);

        Assert.That(Add(tree, SecondBit, 100), Is.EqualTo(BucketAddResult.Added));
        Assert.That(Add(tree, SecondBit | 1, 101), Is.EqualTo(BucketAddResult.Added));

        // The root is full, so this insert splits it; the node lands in the empty right child,
        // which holds no same-subnet nodes even though the pre-split leaf was saturated.
        Assert.That(Add(tree, HighBit, 102), Is.EqualTo(BucketAddResult.Added));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(HighBit, out _), Is.True);
            Assert.That(limiter.CountFor(1), Is.EqualTo(3));
        }
        AssertCountsMatchActiveEntries(tree, limiter);
    }

    [Test]
    public void Rejected_refresh_does_not_evict_the_incumbent()
    {
        SubnetLimiter limiter = new(bucketLimit: 2, tableLimit: 10);
        KBucketTree<int, int> tree = CreateTree(k: 16, limiter: limiter);
        int added = 0;
        tree.OnNodeAdded += (_, _) => added++;

        Add(tree, 1000, 100);
        Add(tree, 1001, 101);

        BucketAddResult rejected = tree.TryAddOrRefresh(1002, 102, out int toRefresh);
        BucketAddResult rejectedAgain = tree.TryAddOrRefresh(1002, 102, out int toRefreshAgain);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejected, Is.EqualTo(BucketAddResult.Rejected));
            Assert.That(rejectedAgain, Is.EqualTo(BucketAddResult.Rejected));
            Assert.That(toRefresh, Is.EqualTo(0));
            Assert.That(toRefreshAgain, Is.EqualTo(0));
            Assert.That(added, Is.EqualTo(2));
            Assert.That(limiter.ReleaseCalls, Is.EqualTo(0));
            Assert.That(tree.TryGet(1000, out _), Is.True);
            Assert.That(tree.TryGet(1001, out _), Is.True);
            Assert.That(tree.TryGet(1002, out _), Is.False);
        }
    }

    [Test]
    public void Release_on_remove_lets_a_newcomer_into_the_bucket()
    {
        (KBucketTree<int, int> tree, SubnetLimiter limiter) = CreateSaturatedTree();
        Assert.That(limiter.CountFor(1), Is.EqualTo(2));

        Assert.That(tree.Remove(1000), Is.True);
        Assert.That(limiter.CountFor(1), Is.EqualTo(1));

        Assert.That(Add(tree, 1002, 102), Is.EqualTo(BucketAddResult.Added));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(1000, out _), Is.False);
            Assert.That(tree.TryGet(1002, out _), Is.True);
            Assert.That(limiter.CountFor(1), Is.EqualTo(2));
        }
    }

    [Test]
    public void Promotion_into_a_free_slot_is_admitted()
    {
        SubnetLimiter limiter = new(bucketLimit: 2, tableLimit: 10);
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: -100, limiter: limiter);

        Add(tree, 10, 100);
        Add(tree, 20, 200);
        Assert.That(Add(tree, 30, 300), Is.EqualTo(BucketAddResult.Full));
        Assert.That(limiter.CountFor(3), Is.EqualTo(0));

        Assert.That(tree.Remove(10), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(30, out _), Is.True);
            Assert.That(tree.TryGet(10, out _), Is.False);
            Assert.That(limiter.CountFor(3), Is.EqualTo(1));
        }
    }

    [TestCase(2, 10, new[] { 100, 101, 200 }, new[] { 102 }, 200, 102)]
    [TestCase(3, 2, new[] { 100, 101, 102 }, new[] { 102, 103 }, 102, 103)]
    public void Promotion_discards_a_candidate_that_fails_readmission(
        int bucketLimit,
        int tableLimit,
        int[] activeNodes,
        int[] exemptNodes,
        int removeNode,
        int candidateNode)
    {
        SubnetLimiter limiter = new(bucketLimit, tableLimit);
        KBucketTree<int, int> tree = CreateTree(k: 3, beta: -100, limiter: limiter);

        foreach (int node in activeNodes)
        {
            if (exemptNodes.Contains(node)) limiter.Exempt.Add(node);
            Assert.That(Add(tree, node, node), Is.EqualTo(BucketAddResult.Added));
        }

        limiter.Exempt.Add(candidateNode);
        Assert.That(Add(tree, candidateNode, candidateNode), Is.EqualTo(BucketAddResult.Full));
        limiter.Exempt.Clear();

        Assert.That(tree.Remove(removeNode), Is.True);
        Assert.That(tree.TryGet(candidateNode, out _), Is.False);
        AssertCountsMatchActiveEntries(tree, limiter);
    }

    [Test]
    public void Splitting_a_bucket_does_not_duplicate_counts()
    {
        SubnetLimiter limiter = new(bucketLimit: 2, tableLimit: 10);
        KBucketTree<int, int> tree = CreateTree(k: 2, beta: 32, limiter: limiter);

        Add(tree, SecondBit, 100);
        Add(tree, SecondBit | 1, 101);
        Add(tree, HighBit, 200);

        AssertCountsMatchActiveEntries(tree, limiter);
        Assert.That(limiter.CountFor(1), Is.EqualTo(2));

        Add(tree, HighBit | 1, 102);
        AssertCountsMatchActiveEntries(tree, limiter);
        Assert.That(limiter.CountFor(1), Is.EqualTo(3));
    }

    [Test]
    public void No_limiter_admits_every_node_as_before()
    {
        KBucketTree<int, int> tree = CreateTree(k: 16);
        for (int i = 0; i < 12; i++)
        {
            Assert.That(Add(tree, 1000 + i, 100 + i), Is.EqualTo(BucketAddResult.Added));
            Assert.That(tree.TryGet(1000 + i, out _), Is.True);
        }
    }

    [Test]
    public void Exempt_nodes_bypass_a_saturated_bucket()
    {
        (KBucketTree<int, int> tree, SubnetLimiter limiter) = CreateSaturatedTree();
        limiter.Exempt.Add(102);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Add(tree, 1002, 102), Is.EqualTo(BucketAddResult.Added));
            Assert.That(limiter.CountFor(1), Is.EqualTo(2));
        }
    }

    private static void AssertCountsMatchActiveEntries(KBucketTree<int, int> tree, SubnetLimiter limiter)
    {
        Dictionary<int, int> active = [];
        foreach (int node in tree.IterateBuckets().SelectMany(static bucket => bucket.Nodes))
        {
            int key = node / 100;
            active[key] = active.GetValueOrDefault(key) + 1;
        }

        foreach ((int key, int count) in active)
        {
            Assert.That(limiter.CountFor(key), Is.EqualTo(count), $"count for subnet {key}");
        }

        foreach (int key in limiter.Keys)
        {
            Assert.That(active.GetValueOrDefault(key), Is.EqualTo(limiter.CountFor(key)), $"no leaked charge for subnet {key}");
        }
    }

    /// <summary>
    /// Minimal per-subnet counter used to drive the routing-table hook without duplicating the discovery policy
    /// (which <see cref="DiscoveryNodeLimiterTests"/> covers). Node values 100-199 share one subnet, 200-299 the next.
    /// </summary>
    private sealed class SubnetLimiter(int bucketLimit, int tableLimit) : IRoutingTableNodeLimiter<int>
    {
        private readonly Dictionary<int, int> _counts = [];
        private readonly Dictionary<int, int> _held = [];

        public HashSet<int> Exempt { get; } = [];

        public int ReleaseCalls { get; private set; }

        public int CountFor(int key) => _counts.GetValueOrDefault(key);

        public IEnumerable<int> Keys => _counts.Keys;

        public bool TryAdmit(int node, ReadOnlySpan<int> bucketNodes)
        {
            int key = KeyOf(node);
            if (Exempt.Contains(node))
            {
                Release(node);
                return true;
            }

            if (_held.TryGetValue(node, out int existingKey) && existingKey == key) return true;

            int inBucket = 0;
            foreach (int bucketNode in bucketNodes)
            {
                if (bucketNode == node || Exempt.Contains(bucketNode)) continue;
                if (KeyOf(bucketNode) == key && ++inBucket >= bucketLimit) break;
            }

            if (inBucket >= bucketLimit) return false;
            if (_counts.GetValueOrDefault(key) >= tableLimit) return false;

            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            if (_held.TryGetValue(node, out int previousKey)) Decrement(previousKey);
            _held[node] = key;
            return true;
        }

        public void Release(int node)
        {
            ReleaseCalls++;
            if (_held.Remove(node, out int key)) Decrement(key);
        }

        public bool WouldAdmit(int node) =>
            Exempt.Contains(node) ||
            (_held.TryGetValue(node, out int heldKey) && heldKey == KeyOf(node)) ||
            _counts.GetValueOrDefault(KeyOf(node)) < tableLimit;

        private void Decrement(int key)
        {
            if (!_counts.TryGetValue(key, out int count)) return;
            if (count <= 1)
            {
                _counts.Remove(key);
            }
            else
            {
                _counts[key] = count - 1;
            }
        }

        private static int KeyOf(int node) => node / 100;
    }
}

