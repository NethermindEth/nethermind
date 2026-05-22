// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTreeTests
{
    private static readonly ValueHash256 SelfHash = new("0x0000000000000000000000000000000000000000000000000000000000000000");

    private static KBucketTree<ValueHash256> CreateTree(int k = 4, int beta = 0) =>
        new(
            new KademliaConfig<ValueHash256> { CurrentNodeId = SelfHash, KSize = k, Beta = beta },
            new IdentityNodeHashProvider(),
            LimboLogs.Instance);

    [Test]
    public void Split_should_preserve_lru_order_in_child_buckets()
    {
        KBucketTree<ValueHash256> tree = CreateTree(k: 2, beta: 0);

        ValueHash256 left0 = HashAtDistance(255, 0x10);
        ValueHash256 left1 = HashAtDistance(255, 0x11);
        ValueHash256 right0 = HashAtDistance(254, 0x20);
        ValueHash256 right1 = HashAtDistance(254, 0x21);

        tree.TryAddOrRefresh(left0, left0, out _);
        tree.TryAddOrRefresh(right0, right0, out _);
        tree.TryAddOrRefresh(left1, left1, out _);
        tree.TryAddOrRefresh(right1, right1, out _);

        ValueHash256[] leftBucket = tree.GetAllAtDistance(255);
        ValueHash256[] rightBucket = tree.GetAllAtDistance(254);

        Assert.That(leftBucket[0], Is.EqualTo(left1));
        Assert.That(leftBucket[1], Is.EqualTo(left0));
        Assert.That(rightBucket[0], Is.EqualTo(right1));
        Assert.That(rightBucket[1], Is.EqualTo(right0));
    }

    [Test]
    public void GetAllAtDistance_should_include_nodes_in_deeper_split_buckets()
    {
        KBucketTree<ValueHash256> tree = CreateTree(k: 2, beta: 4);

        ValueHash256 deep1 = HashAtDistance(252, 0x40);
        ValueHash256 deep2 = HashAtDistance(252, 0x41);
        ValueHash256 deep3 = HashAtDistance(252, 0x42);

        tree.TryAddOrRefresh(deep1, deep1, out _);
        tree.TryAddOrRefresh(deep2, deep2, out _);
        tree.TryAddOrRefresh(deep3, deep3, out _);

        HashSet<ValueHash256> atDistance = tree.GetAllAtDistance(252).ToHashSet();
        Assert.That(atDistance, Is.SupersetOf(new[] { deep1, deep2 }));
        Assert.That(atDistance.IsSubsetOf(new[] { deep1, deep2, deep3 }), Is.True);
    }

    private static ValueHash256 HashAtDistance(int distance, byte tag)
    {
        ValueHash256 h = Hash256XorUtils.GetRandomHashAtDistance(SelfHash, distance, new Random(tag));
        return h;
    }

    private sealed class IdentityNodeHashProvider : INodeHashProvider<ValueHash256>
    {
        public ValueHash256 GetHash(ValueHash256 node) => node;
    }
}
