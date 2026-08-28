// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Kademlia;
using NUnit.Framework;

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

        Assert.That(tree.GetAllAtDistance(31), Is.EqualTo(new[] { left1, left0 }));
        Assert.That(tree.GetAllAtDistance(30), Is.EqualTo(new[] { right1, right0 }));
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

        Assert.That(tree.GetByHash(1), Is.EqualTo(2));
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

        Assert.That(tree.TryGet(replacementHash, out int replacement), Is.True);
        Assert.That(replacement, Is.EqualTo(2));

        tree.Remove(activeHash);

        Assert.That(tree.GetByHash(replacementHash), Is.EqualTo(2));
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
}
