// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Network.Discovery.Discv4;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class EndpointBondTableTests
    {
        private static EndpointKey Endpoint(int port) => new(new IPEndPoint(IPAddress.Loopback, port));

        [Test]
        public void Default_table_is_empty()
        {
            EndpointBondTable table = default;

            Assert.That(table.IsEmpty, Is.True);
            Assert.That(table.Count, Is.EqualTo(0));
            Assert.That(table.Contains(Endpoint(1)), Is.False);
            Assert.That(table.HasFresh(Endpoint(1), 0), Is.False);
        }

        [Test]
        public void Operations_on_empty_table_do_not_throw_or_change_state()
        {
            EndpointBondTable table = default;

            Assert.That(table.Remove(Endpoint(1), 123), Is.False);
            Assert.DoesNotThrow(() => table.PruneStale(long.MaxValue));
            Assert.That(table.IsEmpty, Is.True);
        }

        [Test]
        public void Record_adds_new_endpoint()
        {
            EndpointBondTable table = default;

            Assert.That(table.Record(Endpoint(1), stamp: 10), Is.False);

            Assert.That(table.IsEmpty, Is.False);
            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.Contains(Endpoint(1)), Is.True);
        }

        [Test]
        public void Record_refreshes_existing_endpoint_without_growing()
        {
            EndpointBondTable table = default;
            table.Record(Endpoint(1), stamp: 10);

            Assert.That(table.Record(Endpoint(1), stamp: 20), Is.False);

            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.HasFresh(Endpoint(1), minValidStamp: 15), Is.True, "should reflect the refreshed stamp");
        }

        [Test]
        public void Endpoints_differ_by_address_and_port()
        {
            EndpointKey a = new(IPAddress.Parse("10.0.0.1"), 30303);
            EndpointKey samePortDifferentAddress = new(IPAddress.Parse("10.0.0.2"), 30303);
            EndpointKey sameAddressDifferentPort = new(IPAddress.Parse("10.0.0.1"), 30304);

            EndpointBondTable table = default;
            table.Record(a, stamp: 1);

            Assert.That(table.Contains(a), Is.True);
            Assert.That(table.Contains(samePortDifferentAddress), Is.False);
            Assert.That(table.Contains(sameAddressDifferentPort), Is.False);
            Assert.That(table.Count, Is.EqualTo(1));
        }

        [TestCase(9L, true)]
        [TestCase(10L, false)]
        [TestCase(11L, false)]
        public void HasFresh_uses_a_strict_greater_than_threshold(long minValidStamp, bool expected)
        {
            EndpointBondTable table = default;
            table.Record(Endpoint(1), stamp: 10);

            Assert.That(table.HasFresh(Endpoint(1), minValidStamp), Is.EqualTo(expected));
        }

        [Test]
        public void HasFresh_only_matches_the_queried_endpoint()
        {
            EndpointBondTable table = default;
            table.Record(Endpoint(1), stamp: 100);

            Assert.That(table.HasFresh(Endpoint(1), minValidStamp: 0), Is.True);
            Assert.That(table.HasFresh(Endpoint(2), minValidStamp: 0), Is.False);
        }

        [Test]
        public void Contains_ignores_stamp_freshness()
        {
            EndpointBondTable table = default;
            table.Record(Endpoint(1), stamp: 5);

            // Contains never applies the freshness threshold; only HasFresh does.
            Assert.That(table.Contains(Endpoint(1)), Is.True);
            Assert.That(table.HasFresh(Endpoint(1), minValidStamp: 10), Is.False);
        }

        [Test]
        public void Remove_requires_a_matching_stamp()
        {
            EndpointBondTable table = default;
            table.Record(Endpoint(1), stamp: 42);

            Assert.That(table.Remove(Endpoint(1), expectedStamp: 41), Is.False, "stale token must not remove a newer entry");
            Assert.That(table.Contains(Endpoint(1)), Is.True);

            Assert.That(table.Remove(Endpoint(1), expectedStamp: 42), Is.True);
            Assert.That(table.Contains(Endpoint(1)), Is.False);
            Assert.That(table.IsEmpty, Is.True);
        }

        [Test]
        public void Remove_preserves_other_entries()
        {
            EndpointBondTable table = default;
            table.Record(Endpoint(1), stamp: 1);
            table.Record(Endpoint(2), stamp: 2);
            table.Record(Endpoint(3), stamp: 3);

            Assert.That(table.Remove(Endpoint(2), expectedStamp: 2), Is.True);

            Assert.That(table.Count, Is.EqualTo(2));
            Assert.That(table.Contains(Endpoint(1)), Is.True);
            Assert.That(table.Contains(Endpoint(2)), Is.False);
            Assert.That(table.Contains(Endpoint(3)), Is.True);
        }

        [Test]
        public void PruneStale_drops_entries_at_or_below_threshold_and_keeps_fresher_ones()
        {
            EndpointBondTable table = default;
            table.Record(Endpoint(1), stamp: 5);   // stale (== threshold)
            table.Record(Endpoint(2), stamp: 3);   // stale (< threshold)
            table.Record(Endpoint(3), stamp: 9);   // fresh

            table.PruneStale(minValidStamp: 5);

            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.Contains(Endpoint(1)), Is.False);
            Assert.That(table.Contains(Endpoint(2)), Is.False);
            Assert.That(table.Contains(Endpoint(3)), Is.True);
        }

        [Test]
        public void Record_stays_within_capacity()
        {
            EndpointBondTable table = default;

            for (int i = 0; i < EndpointBondTable.Capacity; i++)
            {
                Assert.That(table.Record(Endpoint(i), stamp: i), Is.False, "no eviction while capacity remains");
            }

            Assert.That(table.Count, Is.EqualTo(EndpointBondTable.Capacity));

            Assert.That(table.Record(Endpoint(EndpointBondTable.Capacity), stamp: EndpointBondTable.Capacity), Is.True, "table is full");
            Assert.That(table.Count, Is.EqualTo(EndpointBondTable.Capacity), "count is bounded by capacity");
        }

        [Test]
        public void Record_evicts_the_lowest_stamped_entry_when_full()
        {
            EndpointBondTable table = default;

            // Fill to capacity; the endpoint at port 0 carries the lowest stamp but is not the last inserted.
            table.Record(Endpoint(0), stamp: 1);
            for (int i = 1; i < EndpointBondTable.Capacity; i++)
            {
                table.Record(Endpoint(i), stamp: 100 + i);
            }

            EndpointKey newest = Endpoint(EndpointBondTable.Capacity);
            Assert.That(table.Record(newest, stamp: 500), Is.True);

            Assert.That(table.Contains(Endpoint(0)), Is.False, "lowest-stamped entry is evicted");
            Assert.That(table.Contains(newest), Is.True);
            for (int i = 1; i < EndpointBondTable.Capacity; i++)
            {
                Assert.That(table.Contains(Endpoint(i)), Is.True, $"higher-stamped entry {i} is retained");
            }
        }

        [Test]
        public void Record_refreshes_existing_endpoint_without_eviction_when_full()
        {
            EndpointBondTable table = default;
            for (int i = 0; i < EndpointBondTable.Capacity; i++)
            {
                table.Record(Endpoint(i), stamp: i);
            }

            Assert.That(table.Record(Endpoint(0), stamp: 500), Is.False);

            Assert.That(table.Count, Is.EqualTo(EndpointBondTable.Capacity));
            Assert.That(table.HasFresh(Endpoint(0), minValidStamp: 499), Is.True);
            for (int i = 1; i < EndpointBondTable.Capacity; i++)
            {
                Assert.That(table.Contains(Endpoint(i)), Is.True, $"entry {i} should not be evicted");
            }
        }
    }
}
