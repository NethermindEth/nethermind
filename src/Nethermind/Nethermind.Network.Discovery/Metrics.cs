// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Network.Discovery;

public static class Metrics
{
    [CounterMetric]
    [Description("Number of bytes sent through Discovery (UDP).")]
    public static long DiscoveryBytesSent;

    [CounterMetric]
    [Description("Number of bytes received through Discovery (UDP).")]
    public static long DiscoveryBytesReceived;

    [CounterMetric]
    [Description("Number of sent discovery message")]
    [DetailedMetric]
    [KeyIsLabel("message_type")]
    public static NonBlocking.ConcurrentDictionary<MsgType, long> DiscoveryMessagesSent { get; } = new();

    [CounterMetric]
    [Description("Number of sent discovery messages by protocol")]
    [DetailedMetric]
    [KeyIsLabel("protocol", "message_type")]
    public static NonBlocking.ConcurrentDictionary<DiscoveryMessageKey, long> DiscoveryMessagesSentByProtocol { get; } = new();

    [CounterMetric]
    [Description("Number of sent discovery message")]
    [DetailedMetric]
    [KeyIsLabel("message_type")]
    public static NonBlocking.ConcurrentDictionary<MsgType, long> DiscoveryMessagesReceived { get; } = new();
}
