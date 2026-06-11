// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTreeTests
{
    private static readonly ValueHash256 SelfHash = new("0x0000000000000000000000000000000000000000000000000000000000000000");

    private static KBucketTree<ValueHash256, Hash256> CreateTree(int k = 4, int beta = 0) => new(
        new KademliaConfig<ValueHash256> { CurrentNodeId = SelfHash, KSize = k, Beta = beta },
        IdentityNodeHashProvider.Instance,
        Hash256KademliaDistance.Instance);

    private static void Add(KBucketTree<ValueHash256, Hash256> tree, ValueHash256 hash) =>
        tree.TryAddOrRefresh(IdentityNodeHashProvider.ToHash(hash), hash, out _);

    private static ValueHash256 HashAtDistance(int distance, byte tag) =>
        ToValueHash(Hash256KademliaDistance.Instance.GetRandomHashAtDistance(IdentityNodeHashProvider.ToHash(SelfHash), distance, new Random(tag)));

    private static ValueHash256 ToValueHash(Hash256 hash) => hash.ValueHash256;

    [Test]
    public void Split_should_preserve_lru_order_in_child_buckets()
    {
        KBucketTree<ValueHash256, Hash256> tree = CreateTree(k: 2, beta: 0);

        ValueHash256 left0 = HashAtDistance(255, 0x10);
        ValueHash256 left1 = HashAtDistance(255, 0x11);
        ValueHash256 right0 = HashAtDistance(254, 0x20);
        ValueHash256 right1 = HashAtDistance(254, 0x21);

        Add(tree, left0);
        Add(tree, right0);
        Add(tree, left1);
        Add(tree, right1);

        Assert.That(tree.GetAllAtDistance(255), Is.EqualTo(new[] { left1, left0 }));
        Assert.That(tree.GetAllAtDistance(254), Is.EqualTo(new[] { right1, right0 }));
    }

    [Test]
    public void GetAllAtDistance_should_include_nodes_in_deeper_split_buckets()
    {
        KBucketTree<ValueHash256, Hash256> tree = CreateTree(k: 2, beta: 4);

        ValueHash256 deep1 = HashAtDistance(252, 0x40);
        ValueHash256 deep2 = HashAtDistance(252, 0x41);
        ValueHash256 deep3 = HashAtDistance(252, 0x42);

        Add(tree, deep1);
        Add(tree, deep2);
        Add(tree, deep3);

        ValueHash256[] expectedCandidates = [deep1, deep2, deep3];
        ValueHash256[] result = tree.GetAllAtDistance(252);
        Assert.That(result, Is.SupersetOf(new[] { deep1, deep2 }));
        Assert.That(result.All(expectedCandidates.Contains), Is.True);
    }
}
