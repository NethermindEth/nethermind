// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Metric;

namespace Nethermind.Network;

/// <summary>
/// Identifies a discovery message metric by protocol and message type.
/// </summary>
/// <param name="Protocol">The discovery protocol name.</param>
/// <param name="MessageType">The discovery message type.</param>
public readonly record struct DiscoveryMessageKey(string Protocol, string MessageType) : IMetricLabels
{
    /// <summary>
    /// Gets the metric label values in protocol and message-type order.
    /// </summary>
    public string[] Labels => [Protocol, MessageType];
}
