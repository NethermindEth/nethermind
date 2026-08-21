// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Metric;

namespace Nethermind.Network;

public readonly record struct DiscoveryMessageKey(string Protocol, string MessageType) : IMetricLabels
{
    public string[] Labels => [Protocol, MessageType];
}
