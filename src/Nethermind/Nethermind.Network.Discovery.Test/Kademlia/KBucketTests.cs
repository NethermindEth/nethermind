// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
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
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
        }

        // Again
        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
        }

        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo(toAdd[..5].ToHashSet()));
        Assert.That(bucket.GetAllWithHash().Select(static it => it.Item2).ToHashSet(), Is.EquivalentTo(toAdd[..5].ToHashSet()));

        foreach (ValueHash256 valueHash256 in toAdd[..5])
        {
            Assert.That(bucket.ContainsNode(valueHash256), Is.True);
            Assert.That(bucket.GetByHash(valueHash256), Is.EqualTo(valueHash256));
        }
    }

    [Test]
    public void TryAddOrRefresh_ShouldKeepSameCachedArray_WhenAddingSameNode()
    {
        KBucket<ValueHash256> bucket = new(5);

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
        }

        ValueHash256[] nodes = bucket.GetAll();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
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
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
        }

        bucket.RemoveAndReplace(toAdd[0]);

        Assert.That(bucket.GetAll().ToHashSet(), Is.EquivalentTo((toAdd[1..5].Concat(toAdd[9..10])).ToHashSet()));
        Assert.That(bucket.ContainsNode(toAdd[0]), Is.False);
        Assert.That(bucket.ContainsNode(toAdd[9]), Is.True);
        Assert.That(bucket.GetByHash(toAdd[9]), Is.EqualTo(toAdd[9]));
        Assert.That(bucket.GetByHash(toAdd[0]), Is.Not.EqualTo(toAdd[0]));
    }
}
