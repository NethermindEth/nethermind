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
        KBucket<ValueHash256> bucket = new(5);

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(ToKademliaHash(valueHash256), valueHash256, out _);
        }

        // Again
        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(ToKademliaHash(valueHash256), valueHash256, out _);
        }

        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(toAdd[..5].ToHashSet()));
        Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(toAdd[..5].Select(static it => (ToKademliaHash(it), it)).ToHashSet()));

        foreach (ValueHash256 valueHash256 in toAdd[..5])
        {
            Assert.That(bucket.ContainsNode(ToKademliaHash(valueHash256)), Is.True);
            Assert.That(bucket.GetByHash(ToKademliaHash(valueHash256)), Is.EqualTo(valueHash256));
        }
    }

    [Test]
    public void TryAddOrRefresh_ShouldKeepSameCachedArray_WhenAddingSameNode()
    {
        KBucket<ValueHash256> bucket = new(5);

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(ToKademliaHash(valueHash256), valueHash256, out _);
        }

        ValueHash256[] nodes = bucket.GetAll();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(ToKademliaHash(valueHash256), valueHash256, out _);
        }

        Assert.That(bucket.GetAll(), Is.SameAs(nodes));
    }

    [Test]
    public void RemoveAndReplace_ShouldReplaceNodeWithLatestInReplacementCache()
    {
        KBucket<ValueHash256> bucket = new(5);

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(ToKademliaHash(valueHash256), valueHash256, out _);
        }

        bucket.RemoveAndReplace(ToKademliaHash(toAdd[0]));

        ValueHash256[] expected = [.. toAdd[1..5], toAdd[9]];
        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(expected.ToHashSet()));
        Assert.That(bucket.GetAllWithHash().ToHashSet(), Is.EquivalentTo(expected.Select(static it => (ToKademliaHash(it), it)).ToHashSet()));
    }

    private static KademliaHash ToKademliaHash(ValueHash256 hash) => KademliaHash.FromBytes(hash.BytesAsSpan);
}
