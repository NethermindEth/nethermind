// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

#pragma warning disable NUnit2045 // Assertions are interleaved with bucket mutations; a failure must stop the sequence.

public class KBucketTests
{
    [Test]
    public void TryAddOrRefresh_ShouldLimitToK()
    {
        (KBucket<int, int> bucket, int[] toAdd) = BuildFullBucket();

        // Again
        AddNodes(bucket, toAdd);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(toAdd[..5].ToHashSet()));
            Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(toAdd[..5].Select(static it => (it, it)).ToHashSet()));
        }

        foreach (int node in toAdd[..5])
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bucket.ContainsNode(node), Is.True);
                Assert.That(bucket.GetByHash(node), Is.EqualTo(node));
            }
        }
    }

    [Test]
    public void GetAll_should_return_snapshot_when_adding_same_node()
    {
        (KBucket<int, int> bucket, int[] toAdd) = BuildFullBucket();

        int[] nodes = bucket.GetAll();

        AddNodes(bucket, toAdd);

        int[] refreshedNodes = bucket.GetAll();
        Assert.That(refreshedNodes, Is.Not.SameAs(nodes));
        Assert.That(refreshedNodes.ToHashSet(), Is.EquivalentTo(nodes.ToHashSet()));
    }

    [Test]
    public void GetAll_should_not_keep_cached_array_for_large_bucket()
    {
        KBucket<int, int> bucket = new(KBucket<int, int>.DefaultReplacementCacheSize + 1);
        AddNodes(bucket, Enumerable.Range(0, KBucket<int, int>.DefaultReplacementCacheSize + 1).ToArray());

        int[] nodes = bucket.GetAll();

        Assert.That(bucket.GetAll(), Is.Not.SameAs(nodes));
    }

    [Test]
    public void TryAddOrRefresh_ShouldReplaceCachedNode_WhenRefreshingSameHashWithNewInstance()
    {
        KBucket<int, int> bucket = new(5);
        const int hash = 1;

        bucket.TryAddOrRefresh(hash, 10, out _);
        bucket.TryAddOrRefresh(hash, 11, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bucket.GetByHash(hash), Is.EqualTo(11));
            Assert.That(bucket.GetAll(), Is.EqualTo(new[] { 11 }));
            Assert.That(bucket.GetAllWithHash(), Is.EqualTo(new[] { (hash, 11) }));
        }
    }

    [Test]
    public void Remove_and_TryPromote_should_replace_node_with_latest_in_replacement_cache()
    {
        (KBucket<int, int> bucket, int[] toAdd) = BuildFullBucket();

        Assert.That(bucket.Remove(toAdd[0]), Is.True);
        Assert.That(bucket.TryPromote(null), Is.True);

        int[] expected = [.. toAdd[1..5], toAdd[9]];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(expected.ToHashSet()));
            Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(expected.Select(static it => (it, it)).ToHashSet()));
        }
    }

    [Test]
    public void Replacement_cache_should_not_scale_with_large_bucket_size()
    {
        const int bucketSize = KBucket<int, int>.DefaultReplacementCacheSize * 2;

        KBucket<int, int> bucket = new(bucketSize);
        int[] nodes = Enumerable.Range(0, bucketSize + KBucket<int, int>.DefaultReplacementCacheSize + 1).ToArray();

        AddNodes(bucket, nodes);
        foreach (int node in nodes[..bucketSize])
        {
            bucket.Remove(node);
            bucket.TryPromote(null);
        }

        Assert.That(bucket.Count, Is.EqualTo(KBucket<int, int>.DefaultReplacementCacheSize));
    }

    [Test]
    public void Remove_should_reject_an_unknown_hash()
    {
        KBucket<int, int> bucket = new(5);
        AddNodes(bucket, [1, 2]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bucket.Remove(3), Is.False);
            Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(new[] { 1, 2 }));
        }
    }

    [TestCase(new[] { 0, 1, 2, 3 }, 0, false, new[] { 1, 3 }, true, true)]
    [TestCase(new[] { 0, 1, 2, 3 }, 0, true, new[] { 1, 2 }, false, true)]
    [TestCase(new[] { 0, 1 }, 1, false, new[] { 0 }, false, false)]
    public void TryPromote_should_promote_the_newest_accepted_candidate(
        int[] added,
        int removed,
        bool rejectNewest,
        int[] expected,
        bool secondPromoteResult,
        bool firstPromoteResult)
    {
        KBucket<int, int> bucket = new(2);
        AddNodes(bucket, added);

        Assert.That(bucket.Remove(removed), Is.True);
        Assert.That(bucket.TryPromote(rejectNewest ? static node => node != 3 : null), Is.EqualTo(firstPromoteResult));
        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(expected.ToHashSet()));

        // The second promotion reports whether the cache still holds a candidate: a rejected
        // head was popped, not left in place, and an empty cache promotes nothing.
        Assert.That(bucket.TryPromote(null), Is.EqualTo(secondPromoteResult));
    }

    private static (KBucket<int, int> Bucket, int[] Nodes) BuildFullBucket()
    {
        KBucket<int, int> bucket = new(5);
        int[] nodes = Enumerable.Range(0, 10).ToArray();
        AddNodes(bucket, nodes);
        return (bucket, nodes);
    }

    private static void AddNodes(KBucket<int, int> bucket, int[] nodes)
    {
        foreach (int node in nodes)
        {
            bucket.TryAddOrRefresh(node, node, out _);
        }
    }
}
