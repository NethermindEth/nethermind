// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTests
{
    [Test]
    public void TryAddOrRefresh_ShouldLimitToK()
    {
        (KBucket<ValueHash256, Hash256> bucket, ValueHash256[] toAdd) = BuildFullBucket();

        // Again
        AddNodes(bucket, toAdd);

        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(toAdd[..5].ToHashSet()));
        Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(toAdd[..5].Select(static it => (IdentityNodeHashProvider.ToHash(it), it)).ToHashSet()));

        foreach (ValueHash256 valueHash256 in toAdd[..5])
        {
            Assert.That(bucket.ContainsNode(IdentityNodeHashProvider.ToHash(valueHash256)), Is.True);
            Assert.That(bucket.GetByHash(IdentityNodeHashProvider.ToHash(valueHash256)), Is.EqualTo(valueHash256));
        }
    }

    [Test]
    public void TryAddOrRefresh_ShouldKeepSameCachedArray_WhenAddingSameNode()
    {
        (KBucket<ValueHash256, Hash256> bucket, ValueHash256[] toAdd) = BuildFullBucket();

        ValueHash256[] nodes = bucket.GetAll();

        AddNodes(bucket, toAdd);

        Assert.That(bucket.GetAll(), Is.SameAs(nodes));
    }

    [Test]
    public void TryAddOrRefresh_ShouldReplaceCachedNode_WhenRefreshingSameHashWithNewInstance()
    {
        KBucket<string, Hash256> bucket = new(5);
        Hash256 hash = IdentityNodeHashProvider.ToHash(ValueKeccak.Compute("node"));

        bucket.TryAddOrRefresh(hash, "old", out _);
        bucket.TryAddOrRefresh(hash, "new", out _);

        Assert.That(bucket.GetByHash(hash), Is.EqualTo("new"));
        Assert.That(bucket.GetAll(), Is.EqualTo(new[] { "new" }));
        Assert.That(bucket.GetAllWithHash(), Is.EqualTo(new[] { (hash, "new") }));
    }

    [Test]
    public void RemoveAndReplace_ShouldReplaceNodeWithLatestInReplacementCache()
    {
        (KBucket<ValueHash256, Hash256> bucket, ValueHash256[] toAdd) = BuildFullBucket();

        bucket.RemoveAndReplace(IdentityNodeHashProvider.ToHash(toAdd[0]));

        ValueHash256[] expected = [.. toAdd[1..5], toAdd[9]];
        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(expected.ToHashSet()));
        Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(expected.Select(static it => (IdentityNodeHashProvider.ToHash(it), it)).ToHashSet()));
    }

    private static (KBucket<ValueHash256, Hash256> Bucket, ValueHash256[] Nodes) BuildFullBucket()
    {
        KBucket<ValueHash256, Hash256> bucket = new(5);
        ValueHash256[] nodes = Enumerable.Range(0, 10).Select(static k => ValueKeccak.Compute(k.ToString())).ToArray();
        AddNodes(bucket, nodes);
        return (bucket, nodes);
    }

    private static void AddNodes(KBucket<ValueHash256, Hash256> bucket, ValueHash256[] nodes)
    {
        foreach (ValueHash256 node in nodes)
        {
            bucket.TryAddOrRefresh(IdentityNodeHashProvider.ToHash(node), node, out _);
        }
    }
}
