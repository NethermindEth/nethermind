// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Api;

/// <inheritdoc cref="IBeaconApiConfig"/>
public class BeaconApiConfig : IBeaconApiConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5052;
    public int MaxConcurrentStateRequests { get; set; } = 2;
    public int StateDownloadsPerMinutePerClient { get; set; } = 30;
    public int MaxConcurrentStateRequestsPerClient { get; set; } = 1;
    public int StateResponseTimeoutSeconds { get; set; } = 3600;
    public int StateResponseIdleTimeoutSeconds { get; set; } = 120;
}
