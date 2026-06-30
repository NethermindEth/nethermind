// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTreeTests
{
    private const int SelfHash = 0;

    private static KBucketTree<int, int> CreateTree(int k = 4, int beta = 0) => new(
        new KademliaConfig<int> { CurrentNodeId = SelfHash, KSize = k, Beta = beta },
        IntNodeHashProvider.Instance,
        Int32KademliaDistance.Instance,
        NullLoggerFactory.Instance);

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

    private static int KeyAtDistance(int distance, int suffix)
        => Int32KademliaDistance.Instance.SetBit(suffix, Int32KademliaDistance.Instance.MaxDistance - distance);
}
