// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.RoutingTable
{
    [TestFixture]
    public class NodeBucketTests
    {
        private Node _node = new(TestItem.PublicKeyA, IPAddress.Broadcast.ToString(), 30000);
        private Node _node2 = new(TestItem.PublicKeyB, IPAddress.Broadcast.ToString(), 3000);
        private Node _node3 = new(TestItem.PublicKeyC, IPAddress.Broadcast.ToString(), 3000);

        [Test]
        public void Bonded_count_is_tracked()
        {
            NodeBucket nodeBucket = new(1, 16);
            nodeBucket.AddNode(_node);
            nodeBucket.AddNode(_node2);
            nodeBucket.AddNode(_node3);
            nodeBucket.BondedItemsCount.Should().Be(3);
        }

        [Test]
        public void Newly_added_can_be_retrieved_as_bonded()
        {
            NodeBucket nodeBucket = new(1, 16);
            nodeBucket.AddNode(_node);
            nodeBucket.AddNode(_node2);
            nodeBucket.AddNode(_node3);
            nodeBucket.BondedItems.Should().HaveCount(3);
        }

        [Test]
        public void Distance_is_set_properly()
        {
            NodeBucket nodeBucket = new(1, 16);
            nodeBucket.Distance.Should().Be(1);
        }

        [Test]
        public void Limits_the_bucket_size()
        {
            NodeBucket nodeBucket = new(1, 16);
            AddNodes(nodeBucket, 32);

            nodeBucket.BondedItemsCount.Should().Be(16);
            nodeBucket.BondedItems.Should().HaveCount(16);
        }

        [Test]
        public void Can_replace_existing_when_full()
        {
            NodeBucket nodeBucket = new(1, 16);
            AddNodes(nodeBucket, 32);

            Node node = new(
                TestItem.PublicKeyA,
                IPAddress.Broadcast.ToString(),
                30001);

            Node existing = nodeBucket.BondedItems.First().Node!;
            nodeBucket.ReplaceNode(existing, node);
            nodeBucket.BondedItemsCount.Should().Be(16);
            nodeBucket.BondedItems.Should().HaveCount(16);
            nodeBucket.BondedItems.Should().Contain(bi => bi.Node == node);
            nodeBucket.BondedItems.Should().NotContain(bi => bi.Node == existing);
        }

        [TestCase(2)]
        [TestCase(5)]
        [TestCase(32)]
        public void Can_refresh(int nodesInTheBucket)
        {
            NodeBucket nodeBucket = new(1, 16);
            AddNodes(nodeBucket, nodesInTheBucket);

            Node existing1 = nodeBucket.BondedItems.First().Node!;
            nodeBucket.RefreshNode(existing1);

            nodeBucket.BondedItems.Should().HaveCount(Math.Min(nodeBucket.BucketSize, nodesInTheBucket));
        }

        [TestCase(0)]
        [TestCase(5)]
        [TestCase(32)]
        public void Throws_when_replacing_non_existing(int nodesInTheBucket)
        {
            NodeBucket nodeBucket = new(1, 16);
            AddNodes(nodeBucket, nodesInTheBucket);

            Node node = new(
                TestItem.PublicKeyA,
                IPAddress.Broadcast.ToString(),
                30001);

            Node nonExisting = new(
                TestItem.PublicKeyA,
                IPAddress.Broadcast.ToString(),
                30002);

            Assert.DoesNotThrow(() => nodeBucket.ReplaceNode(nonExisting, node));
        }

        [Test]
        public void When_addingToFullBucket_then_randomlyDropEntry()
        {
            NodeBucket nodeBucket = new(1, 16, dropFullBucketProbability: .5f);
            for (int i = 0; i < 16; i++)
            {
                Node node = new(
                    TestItem.PublicKeys[i],
                    IPAddress.Broadcast.ToString(),
                    30000);

                nodeBucket.AddNode(node);
            }

            int dropCount = 0;
            for (int i = 0; i < 100; i++)
            {
                Node node = new(
                    TestItem.PublicKeys[i + 16],
                    IPAddress.Broadcast.ToString(),
                    30000);

                if (nodeBucket.AddNode(node).ResultType == NodeAddResultType.Dropped)
                {
                    dropCount++;
                }
            }

            dropCount.Should().BeInRange(25, 75);
        }

        private static void AddNodes(NodeBucket nodeBucket, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Node node = new(
                    TestItem.PublicKeys[i],
                    IPAddress.Broadcast.ToString(),
                    30000);

                nodeBucket.AddNode(node);
            }
        }
    }
}
