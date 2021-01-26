//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Linq;
using System.Net;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery.RoutingTable
{
    [TestFixture]
    public class NodeBucketTests
    {
        private Node _node = new Node(TestItem.PublicKeyA, IPAddress.Broadcast.ToString(), 30000);
        private Node _node2 = new Node(TestItem.PublicKeyB, IPAddress.Broadcast.ToString(), 30000);
        private Node _node3 = new Node(TestItem.PublicKeyC, IPAddress.Broadcast.ToString(), 30000);
        
        [Test]
        public void Bonded_count_is_tracked()
        {
            NodeBucket nodeBucket = new NodeBucket(1, 16);
            nodeBucket.AddNode(_node);
            nodeBucket.AddNode(_node2);
            nodeBucket.AddNode(_node3);
            nodeBucket.BondedItemsCount.Should().Be(3);
        }
        
        [Test]
        public void Newly_added_can_be_retrieved_as_bonded()
        {
            NodeBucket nodeBucket = new NodeBucket(1, 16);
            nodeBucket.AddNode(_node);
            nodeBucket.AddNode(_node2);
            nodeBucket.AddNode(_node3);
            nodeBucket.BondedItems.Should().HaveCount(3);
        }
        
        [Test]
        public void Distance_is_set_properly()
        {
            NodeBucket nodeBucket = new NodeBucket(1, 16);
            nodeBucket.Distance.Should().Be(1);
        }
        
        [Test]
        public void Limits_the_bucket_size()
        {
            NodeBucket nodeBucket = new NodeBucket(1, 16);
            AddNodes(nodeBucket, 32);
            
            nodeBucket.BondedItemsCount.Should().Be(16);
            nodeBucket.BondedItems.Should().HaveCount(16);
        }
        
        [Test]
        public void Can_replace_existing_when_full()
        {
            NodeBucket nodeBucket = new NodeBucket(1, 16);
            AddNodes(nodeBucket, 32);
            
            Node node = new Node(
                TestItem.PublicKeyA,
                IPAddress.Broadcast.ToString(),
                30001);

            Node existing = nodeBucket.BondedItems.First().Node;
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
            NodeBucket nodeBucket = new NodeBucket(1, 16);
            AddNodes(nodeBucket, nodesInTheBucket);

            Node existing1 = nodeBucket.BondedItems.First().Node;
            nodeBucket.RefreshNode(existing1);

            nodeBucket.BondedItems.Should().HaveCount(Math.Min(nodeBucket.BucketSize, nodesInTheBucket));
        }
        
        [TestCase(0)]
        [TestCase(5)]
        [TestCase(32)]
        public void Throws_when_replacing_non_existing(int nodesInTheBucket)
        {
            NodeBucket nodeBucket = new NodeBucket(1, 16);
            AddNodes(nodeBucket, nodesInTheBucket);
            
            Node node = new Node(
                TestItem.PublicKeyA,
                IPAddress.Broadcast.ToString(),
                30001);
            
            Node nonExisting = new Node(
                TestItem.PublicKeyA,
                IPAddress.Broadcast.ToString(),
                30002);
            
            Assert.Throws<InvalidOperationException>(() => nodeBucket.ReplaceNode(nonExisting, node));
        }

        private static void AddNodes(NodeBucket nodeBucket, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Node node = new Node(
                    TestItem.PublicKeys[i],
                    IPAddress.Broadcast.ToString(),
                    30000);

                nodeBucket.AddNode(node);
            }
        }
    }
}
