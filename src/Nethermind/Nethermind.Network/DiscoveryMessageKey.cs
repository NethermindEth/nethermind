// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Metric;

namespace Nethermind.Network;

public readonly record struct DiscoveryMessageKey(string Protocol, string MessageType) : IMetricLabels
{
    private static readonly ConcurrentDictionary<DiscoveryMessageKey, string[]> s_labelCache = new();

    public string[] Labels => s_labelCache.GetOrAdd(this, static key => [key.Protocol, key.MessageType]);
}
