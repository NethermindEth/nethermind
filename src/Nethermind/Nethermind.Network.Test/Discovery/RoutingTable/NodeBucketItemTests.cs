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
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery.RoutingTable
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class NodeBucketItemTests
    {
        [Test]
        public void Last_contacted_time_is_set_to_now_at_the_beginning()
        {
            Node node = new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new NodeBucketItem(node, DateTime.UtcNow);
            nodeBucketItem.LastContactTime.Should().BeAfter(DateTime.UtcNow.AddDays(-1));
        }

        [Test]
        public async Task On_contact_received_we_update_last_contacted_date()
        {
            Node node = new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new NodeBucketItem(node, DateTime.UtcNow);

            DateTime dateTime = nodeBucketItem.LastContactTime;
            await Task.Delay(10);
            nodeBucketItem.OnContactReceived();
            DateTime dateTime2 = nodeBucketItem.LastContactTime;
            dateTime2.Should().BeAfter(dateTime);
        }

        [Test]
        public void Is_bonded_at_start()
        {
            Node node = new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new NodeBucketItem(node, DateTime.UtcNow);
            nodeBucketItem.IsBonded.Should().BeTrue();
        }

        [Test]
        public void Two_with_same_node_are_equal()
        {
            Node node = new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);

            NodeBucketItem nodeBucketItem = new NodeBucketItem(node, DateTime.UtcNow);
            NodeBucketItem nodeBucketItem2 = new NodeBucketItem(node, DateTime.UtcNow);
            nodeBucketItem.Should().Be(nodeBucketItem2);
        }
        
        [Test]
        public void Different_should_not_be_equal()
        {
            Node node = new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            Node node2 = new Node(TestItem.PublicKeyB, IPAddress.Loopback.ToString(), 30000);

            NodeBucketItem nodeBucketItem = new NodeBucketItem(node, DateTime.UtcNow);
            NodeBucketItem nodeBucketItem2 = new NodeBucketItem(node2, DateTime.UtcNow);
            nodeBucketItem.Should().NotBe(nodeBucketItem2);
        }
        
        [Test]
        public void Two_with_same_node_have_same_hash_code()
        {
            Node node = new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);

            NodeBucketItem nodeBucketItem = new NodeBucketItem(node, DateTime.UtcNow);
            NodeBucketItem nodeBucketItem2 = new NodeBucketItem(node, DateTime.UtcNow);
            nodeBucketItem.GetHashCode().Should().Be(nodeBucketItem2.GetHashCode());
        }
        
        [Test]
        public void Same_are_equal()
        {
            Node node = new Node(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new NodeBucketItem(node, DateTime.UtcNow);
            nodeBucketItem.Should().Be(nodeBucketItem);
        }
    }
}
