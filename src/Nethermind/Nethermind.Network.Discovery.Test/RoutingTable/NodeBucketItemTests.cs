// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.RoutingTable
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class NodeBucketItemTests
    {
        [Test]
        public void Last_contacted_time_is_set_to_now_at_the_beginning()
        {
            Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new(node, DateTime.UtcNow);
            nodeBucketItem.LastContactTime.Should().BeAfter(DateTime.UtcNow.AddDays(-1));
        }

        [Test]
        public async Task On_contact_received_we_update_last_contacted_date()
        {
            Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new(node, DateTime.UtcNow);

            DateTime dateTime = nodeBucketItem.LastContactTime;
            await Task.Delay(10);
            nodeBucketItem.OnContactReceived();
            DateTime dateTime2 = nodeBucketItem.LastContactTime;
            dateTime2.Should().BeAfter(dateTime);
        }

        [Test]
        public void Is_bonded_at_start()
        {
            Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new(node, DateTime.UtcNow);
            nodeBucketItem.IsBonded(DateTime.UtcNow).Should().BeTrue();
        }

        [Test]
        public void Two_with_same_node_are_equal()
        {
            Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);

            NodeBucketItem nodeBucketItem = new(node, DateTime.UtcNow);
            NodeBucketItem nodeBucketItem2 = new(node, DateTime.UtcNow);
            nodeBucketItem.Should().Be(nodeBucketItem2);
        }

        [Test]
        public void Different_should_not_be_equal()
        {
            Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            Node node2 = new(TestItem.PublicKeyB, IPAddress.Loopback.ToString(), 30000);

            NodeBucketItem nodeBucketItem = new(node, DateTime.UtcNow);
            NodeBucketItem nodeBucketItem2 = new(node2, DateTime.UtcNow);
            nodeBucketItem.Should().NotBe(nodeBucketItem2);
        }

        [Test]
        public void Two_with_same_node_have_same_hash_code()
        {
            Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);

            NodeBucketItem nodeBucketItem = new(node, DateTime.UtcNow);
            NodeBucketItem nodeBucketItem2 = new(node, DateTime.UtcNow);
            nodeBucketItem.GetHashCode().Should().Be(nodeBucketItem2.GetHashCode());
        }

        [Test]
        public void Same_are_equal()
        {
            Node node = new(TestItem.PublicKeyA, IPAddress.Loopback.ToString(), 30000);
            NodeBucketItem nodeBucketItem = new(node, DateTime.UtcNow);
            nodeBucketItem.Should().Be(nodeBucketItem);
        }
    }
}
