// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Prometheus;
using Nethermind.Network;
using Nethermind.Network.Discovery.Discv4.Messages;
using System.Globalization;
using PrometheusMetrics = Prometheus.Metrics;
using NetworkMetrics = Nethermind.Network.Metrics;

namespace Nethermind.Bootnode;

internal sealed class BootnodeMetrics
{
    private readonly Lock _messageMetricsLock = new();
    private readonly Lock _trafficMetricsLock = new();
    private readonly Lock _bucketMetricsLock = new();
    private readonly Lock _identityMetricsLock = new();
    private readonly Dictionary<DiscoveryMessageKey, long> _lastDiscoveryMessagesSent = [];
    private long _lastDiscoveryBytesSent;
    private long _lastDiscoveryBytesReceived;
    private readonly HashSet<BucketMetricKey> _publishedBuckets = [];
    private IdentityMetricKey? _publishedIdentity;

    private static readonly Gauge ActiveNodes = PrometheusMetrics.CreateGauge(
        "nethermind_bootnode_active_nodes",
        "Number of active discovery nodes tracked by the bootnode.",
        new GaugeConfiguration { LabelNames = ["protocol"] });

    private static readonly Gauge AllNodes = PrometheusMetrics.CreateGauge(
        "nethermind_bootnode_all_nodes",
        "Number of discovery nodes retained by the bootnode process.",
        new GaugeConfiguration { LabelNames = ["protocol"] });

    private static readonly Counter DiscoveredNodes = PrometheusMetrics.CreateCounter(
        "nethermind_bootnode_discovered_nodes_total",
        "Total number of discovery node observations received by the bootnode.",
        new CounterConfiguration { LabelNames = ["protocol"] });

    private static readonly Counter RemovedNodes = PrometheusMetrics.CreateCounter(
        "nethermind_bootnode_removed_nodes_total",
        "Total number of discovery nodes removed from the active table.",
        new CounterConfiguration { LabelNames = ["protocol"] });

    private static readonly Counter DiscoveryMessagesSent = PrometheusMetrics.CreateCounter(
        "nethermind_bootnode_discovery_messages_sent_total",
        "Total number of discovery messages sent by the bootnode.",
        new CounterConfiguration { LabelNames = ["protocol", "message_type"] });

    private static readonly Counter DiscoveryTrafficBytes = PrometheusMetrics.CreateCounter(
        "nethermind_bootnode_discovery_traffic_bytes_total",
        "Total discovery UDP traffic handled by the bootnode.",
        new CounterConfiguration { LabelNames = ["direction"] });

    private static readonly Gauge KademliaBucketNodes = PrometheusMetrics.CreateGauge(
        "nethermind_bootnode_kademlia_bucket_nodes",
        "Number of nodes in each bootnode Kademlia routing-table bucket.",
        new GaugeConfiguration { LabelNames = ["protocol", "bucket", "depth", "prefix"] });

    private static readonly Gauge IdentityInfo = PrometheusMetrics.CreateGauge(
        "nethermind_bootnode_identity_info",
        "Bootnode identity information.",
        new GaugeConfiguration { LabelNames = ["enode", "enr", "seq", "node_id", "address"] });

    public void RecordSeen(string protocol) => DiscoveredNodes.WithLabels(protocol).Inc();

    public void RecordRemoved(string protocol) => RemovedNodes.WithLabels(protocol).Inc();

    public void SetIdentity(BootnodeIdentity identity)
    {
        IdentityMetricKey key = new(
            identity.Enode,
            identity.Enr,
            identity.EnrSequence.ToString(CultureInfo.InvariantCulture),
            identity.NodeId,
            identity.Address);

        lock (_identityMetricsLock)
        {
            if (_publishedIdentity == key)
            {
                return;
            }

            if (_publishedIdentity is { } previous)
            {
                IdentityInfo.WithLabels(previous.Enode, previous.Enr, previous.EnrSequence, previous.NodeId, previous.Address).Unpublish();
            }

            IdentityInfo.WithLabels(key.Enode, key.Enr, key.EnrSequence, key.NodeId, key.Address).Set(1);
            _publishedIdentity = key;
        }
    }

    public void UpdateDiscoveryMessageCounters() =>
        UpdateDiscoveryMessageCounters(NetworkMetrics.DiscoveryMessagesSentByProtocol);

    public void UpdateDiscoveryTrafficCounters() =>
        UpdateDiscoveryTrafficCounters(
            Interlocked.Read(ref NetworkMetrics.DiscoveryBytesSent),
            Interlocked.Read(ref NetworkMetrics.DiscoveryBytesReceived));

    internal long UpdateDiscoveryTrafficCounters(long bytesSent, long bytesReceived)
    {
        lock (_trafficMetricsLock)
        {
            return UpdateDiscoveryTrafficCounter("sent", bytesSent, ref _lastDiscoveryBytesSent)
                + UpdateDiscoveryTrafficCounter("received", bytesReceived, ref _lastDiscoveryBytesReceived);
        }
    }

    internal long UpdateDiscoveryMessageCounters(string protocol, IEnumerable<KeyValuePair<MsgType, long>> messagesSent)
    {
        List<KeyValuePair<DiscoveryMessageKey, long>> protocolMessages = [];
        foreach (KeyValuePair<MsgType, long> messageCounter in messagesSent)
        {
            protocolMessages.Add(new KeyValuePair<DiscoveryMessageKey, long>(
                new DiscoveryMessageKey(protocol, messageCounter.Key.ToString()),
                messageCounter.Value));
        }

        return UpdateDiscoveryMessageCounters(protocolMessages);
    }

    internal long UpdateDiscoveryMessageCounters(IEnumerable<KeyValuePair<DiscoveryMessageKey, long>> messagesSent)
    {
        long totalDelta = 0;

        lock (_messageMetricsLock)
        {
            foreach (KeyValuePair<DiscoveryMessageKey, long> messageCounter in messagesSent)
            {
                long previous = _lastDiscoveryMessagesSent.GetValueOrDefault(messageCounter.Key);
                long delta = messageCounter.Value >= previous
                    ? messageCounter.Value - previous
                    : messageCounter.Value;

                _lastDiscoveryMessagesSent[messageCounter.Key] = messageCounter.Value;

                if (delta <= 0)
                {
                    continue;
                }

                DiscoveryMessagesSent.WithLabels(messageCounter.Key.Protocol, messageCounter.Key.MessageType).Inc(delta);
                totalDelta += delta;
            }
        }

        return totalDelta;
    }

    private static long UpdateDiscoveryTrafficCounter(string direction, long currentBytes, ref long previousBytes)
    {
        long delta = currentBytes >= previousBytes
            ? currentBytes - previousBytes
            : currentBytes;

        previousBytes = currentBytes;

        if (delta <= 0)
        {
            return 0;
        }

        DiscoveryTrafficBytes.WithLabels(direction).Inc(delta);
        return delta;
    }

    public void UpdateKademliaBucketStats(IReadOnlyList<BootnodeKademliaBucketSnapshot> buckets)
    {
        lock (_bucketMetricsLock)
        {
            HashSet<BucketMetricKey> currentBuckets = new(buckets.Count);
            for (int i = 0; i < buckets.Count; i++)
            {
                BootnodeKademliaBucketSnapshot bucket = buckets[i];
                BucketMetricKey key = new(
                    bucket.Protocol,
                    bucket.Bucket.ToString(CultureInfo.InvariantCulture),
                    bucket.Depth.ToString(CultureInfo.InvariantCulture),
                    bucket.Prefix);

                KademliaBucketNodes.WithLabels(key.Protocol, key.Bucket, key.Depth, key.Prefix).Set(bucket.Count);
                currentBuckets.Add(key);
            }

            foreach (BucketMetricKey publishedBucket in _publishedBuckets)
            {
                if (!currentBuckets.Contains(publishedBucket))
                {
                    KademliaBucketNodes
                        .WithLabels(publishedBucket.Protocol, publishedBucket.Bucket, publishedBucket.Depth, publishedBucket.Prefix)
                        .Unpublish();
                }
            }

            _publishedBuckets.Clear();
            foreach (BucketMetricKey currentBucket in currentBuckets)
            {
                _publishedBuckets.Add(currentBucket);
            }
        }
    }

    public void UpdateSnapshot(DiscoverySnapshot snapshot)
    {
        ActiveNodes.WithLabels("all").Set(snapshot.ActiveCount);
        AllNodes.WithLabels("all").Set(snapshot.AllCount);
        ActiveNodes.WithLabels("discv4").Set(snapshot.ActiveDiscv4Count);
        AllNodes.WithLabels("discv4").Set(snapshot.AllDiscv4Count);
        ActiveNodes.WithLabels("discv5").Set(snapshot.ActiveDiscv5Count);
        AllNodes.WithLabels("discv5").Set(snapshot.AllDiscv5Count);
        ActiveNodes.WithLabels("both").Set(snapshot.ActiveBothCount);
        AllNodes.WithLabels("both").Set(snapshot.AllBothCount);
        ActiveNodes.WithLabels("configured").Set(snapshot.ActiveConfiguredCount);
        AllNodes.WithLabels("configured").Set(snapshot.AllConfiguredCount);
    }

    private readonly record struct BucketMetricKey(string Protocol, string Bucket, string Depth, string Prefix);

    private readonly record struct IdentityMetricKey(string Enode, string Enr, string EnrSequence, string NodeId, string Address);
}
