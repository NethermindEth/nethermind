// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
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

        bucket.GetAll().ToHashSet().Should().BeEquivalentTo(toAdd[..5].ToHashSet());
        bucket.GetAllWithHash().Select(it => it.Item2).ToHashSet().Should().BeEquivalentTo(toAdd[..5].ToHashSet());

        foreach (ValueHash256 valueHash256 in toAdd[..5])
        {
            bucket.ContainsNode(valueHash256).Should().BeTrue();
            bucket.GetByHash(valueHash256).Should().NotBeNull();
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

        bucket.GetAll().Should().BeSameAs(nodes);
    }

    [Test]
    public void RemoteAndReplace_ShouldReplaceNodeWithLatestInReplacementCache()
    {
        KBucket<ValueHash256> bucket = new(5);

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
        }

        bucket.RemoveAndReplace(toAdd[0]);

        bucket.GetAll().ToHashSet()
            .Should()
            .BeEquivalentTo((toAdd[1..5].Concat(toAdd[9..10])).ToHashSet());
    }
}
