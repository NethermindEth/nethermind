// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTests
{
    [Test]
    public void TryAddOrRefresh_ShouldLimitToK()
    {
        (KBucket<int, int> bucket, int[] toAdd) = BuildFullBucket();

        // Again
        AddNodes(bucket, toAdd);

        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(toAdd[..5].ToHashSet()));
        Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(toAdd[..5].Select(static it => (it, it)).ToHashSet()));

        foreach (int node in toAdd[..5])
        {
            Assert.That(bucket.ContainsNode(node), Is.True);
            Assert.That(bucket.GetByHash(node), Is.EqualTo(node));
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

        Assert.That(bucket.GetByHash(hash), Is.EqualTo(11));
        Assert.That(bucket.GetAll(), Is.EqualTo(new[] { 11 }));
        Assert.That(bucket.GetAllWithHash(), Is.EqualTo(new[] { (hash, 11) }));
    }

    [Test]
    public void RemoveAndReplace_ShouldReplaceNodeWithLatestInReplacementCache()
    {
        (KBucket<int, int> bucket, int[] toAdd) = BuildFullBucket();

        bucket.RemoveAndReplace(toAdd[0]);

        int[] expected = [.. toAdd[1..5], toAdd[9]];
        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(expected.ToHashSet()));
        Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(expected.Select(static it => (it, it)).ToHashSet()));
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
            bucket.RemoveAndReplace(node);
        }

        Assert.That(bucket.Count, Is.EqualTo(KBucket<int, int>.DefaultReplacementCacheSize));
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
