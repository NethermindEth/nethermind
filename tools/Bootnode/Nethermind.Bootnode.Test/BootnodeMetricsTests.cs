// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network;
using NUnit.Framework;
using System.Text;
using PrometheusMetrics = Prometheus.Metrics;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeMetricsTests
{
    [Test]
    public void UpdateDiscoveryMessageCounters_returns_only_new_sent_messages()
    {
        BootnodeMetrics metrics = new();

        long firstDelta = metrics.UpdateDiscoveryMessageCounters(
        [
            new(new DiscoveryMessageKey("discv4", "Ping"), 5),
            new(new DiscoveryMessageKey("discv4", "FindNode"), 2)
        ]);
        long secondDelta = metrics.UpdateDiscoveryMessageCounters(
        [
            new(new DiscoveryMessageKey("discv4", "Ping"), 8),
            new(new DiscoveryMessageKey("discv4", "FindNode"), 2),
            new(new DiscoveryMessageKey("discv4", "Neighbors"), 4)
        ]);
        long resetDelta = metrics.UpdateDiscoveryMessageCounters(
        [
            new(new DiscoveryMessageKey("discv4", "Ping"), 1)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstDelta, Is.EqualTo(7));
            Assert.That(secondDelta, Is.EqualTo(7));
            Assert.That(resetDelta, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task UpdateDiscoveryMessageCounters_publishes_protocol_label()
    {
        BootnodeMetrics metrics = new();

        long delta = metrics.UpdateDiscoveryMessageCounters(
        [
            new(new DiscoveryMessageKey("discv5", "Ping"), 2),
            new(new DiscoveryMessageKey("discv5", "FindNode"), 3)
        ]);

        string scrape = await ScrapeMetrics();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta, Is.EqualTo(5));
            Assert.That(scrape, Does.Contain("nethermind_bootnode_discovery_messages_sent_total{protocol=\"discv5\",message_type=\"Ping\"}"));
            Assert.That(scrape, Does.Contain("nethermind_bootnode_discovery_messages_sent_total{protocol=\"discv5\",message_type=\"FindNode\"}"));
        }
    }

    [Test]
    public async Task UpdateDiscoveryTrafficCounters_publishes_direction_label()
    {
        BootnodeMetrics metrics = new();

        long firstDelta = metrics.UpdateDiscoveryTrafficCounters(bytesSent: 12, bytesReceived: 7);
        long secondDelta = metrics.UpdateDiscoveryTrafficCounters(bytesSent: 17, bytesReceived: 7);
        long resetDelta = metrics.UpdateDiscoveryTrafficCounters(bytesSent: 2, bytesReceived: 3);

        string scrape = await ScrapeMetrics();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstDelta, Is.EqualTo(19));
            Assert.That(secondDelta, Is.EqualTo(5));
            Assert.That(resetDelta, Is.EqualTo(5));
            Assert.That(scrape, Does.Contain("nethermind_bootnode_discovery_traffic_bytes_total{direction=\"sent\"}"));
            Assert.That(scrape, Does.Contain("nethermind_bootnode_discovery_traffic_bytes_total{direction=\"received\"}"));
        }
    }

    [Test]
    public async Task SetIdentity_replaces_previous_identity_info()
    {
        string id = Guid.NewGuid().ToString("N");
        BootnodeMetrics metrics = new();

        metrics.SetIdentity(new BootnodeIdentity($"enode://old-{id}", $"enr:old-{id}", 1, $"node-old-{id}", $"address-old-{id}"));
        metrics.SetIdentity(new BootnodeIdentity($"enode://new-{id}", $"enr:new-{id}", 2, $"node-new-{id}", $"address-new-{id}"));

        string scrape = await ScrapeMetrics();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scrape, Does.Contain($"enr:new-{id}"));
            Assert.That(scrape, Does.Contain("seq=\"2\""));
            Assert.That(scrape, Does.Not.Contain($"enr:old-{id}"));
        }
    }

    [Test]
    public async Task UpdateKademliaBucketStats_unpublishes_removed_buckets()
    {
        string id = Guid.NewGuid().ToString("N");
        BootnodeMetrics metrics = new();

        metrics.UpdateKademliaBucketStats(
        [
            new BootnodeKademliaBucketSnapshot("discv4", 0, 1, $"prefix-old-{id}", 1),
            new BootnodeKademliaBucketSnapshot("discv4", 1, 1, $"prefix-current-{id}", 2)
        ]);
        metrics.UpdateKademliaBucketStats(
        [
            new BootnodeKademliaBucketSnapshot("discv4", 1, 1, $"prefix-current-{id}", 3)
        ]);

        string scrape = await ScrapeMetrics();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scrape, Does.Contain($"prefix-current-{id}"));
            Assert.That(scrape, Does.Not.Contain($"prefix-old-{id}"));
        }
    }

    [Test]
    public void KademliaBucketRegistry_collects_all_registered_sources()
    {
        BootnodeKademliaBucketRegistry registry = new();
        registry.Register(new StaticBucketSource(new BootnodeKademliaBucketSnapshot("discv4", 0, 1, "prefix-a", 2)));
        registry.Register(new StaticBucketSource(new BootnodeKademliaBucketSnapshot("discv5", 1, 2, "prefix-b", 3)));

        BootnodeKademliaBucketSnapshot[] snapshot = registry.CreateSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot, Has.Length.EqualTo(2));
            Assert.That(snapshot[0].Protocol, Is.EqualTo("discv4"));
            Assert.That(snapshot[1].Protocol, Is.EqualTo("discv5"));
        }
    }

    private static async Task<string> ScrapeMetrics()
    {
        using MemoryStream stream = new();
        await PrometheusMetrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class StaticBucketSource(BootnodeKademliaBucketSnapshot bucket) : IBootnodeKademliaBucketSource
    {
        public void AppendSnapshot(List<BootnodeKademliaBucketSnapshot> snapshot) => snapshot.Add(bucket);
    }
}
