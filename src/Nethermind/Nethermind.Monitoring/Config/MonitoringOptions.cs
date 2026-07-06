// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;

namespace Nethermind.Monitoring.Config;

/// <summary>
/// The monitoring identity (job, group, instance) derived from <see cref="IMetricsConfig"/>.
/// </summary>
/// <remarks>
/// This is the single source of truth for the identity shared by both metric exposition paths:
/// the Pushgateway grouping labels attached by the pusher and the registry static labels attached
/// to the scraped payload. Deriving both from here guarantees they never diverge.
/// </remarks>
public sealed record MonitoringOptions(string Job, string Group, string Instance)
{
    public static MonitoringOptions FromConfig(IMetricsConfig config)
    {
        string? endpoint = config.PushGatewayUrl?.Split('/')[^1];
        string group = endpoint?.Contains('-', StringComparison.Ordinal) == true
            ? endpoint.Split('-')[0]
            : config.MonitoringGroup;
        string instance = (config.NodeName ?? string.Empty)
            .Replace("enode://", string.Empty)
            .Split('@')[0];

        return new MonitoringOptions(config.MonitoringJob, group, instance);
    }
}
