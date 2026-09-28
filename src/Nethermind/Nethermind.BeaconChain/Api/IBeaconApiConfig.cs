// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BeaconChain.Api;

/// <summary>
/// Configuration for the Beacon API REST/SSE host. Deliberately separate from
/// <see cref="IBeaconChainConfig"/>: the API is a self-contained surface with its own listener,
/// not a knob of the sync driver.
/// </summary>
public interface IBeaconApiConfig : IConfig
{
    [ConfigItem(Description = "Whether to serve the Beacon API (REST/SSE) surface.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The host the Beacon API Kestrel listener binds to.", DefaultValue = "\"127.0.0.1\"")]
    string Host { get; set; }

    [ConfigItem(Description = "The port the Beacon API listens on.", DefaultValue = "5052")]
    int Port { get; set; }

    [ConfigItem(Description = "The maximum number of Beacon API requests that read a full beacon state (the /eth/v2/debug/beacon/states routes and the /eth/v1/beacon/states routes other than /root) served at once. Requests beyond it are refused with 503, not queued, and a client address with one such request in flight is refused a second with 429.", DefaultValue = "2")]
    int MaxConcurrentStateRequests { get; set; }

    [ConfigItem(Description = "The maximum number of /eth/v2/debug/beacon/states downloads per minute from one client address (an IPv6 client is counted by its /64 prefix). Requests beyond it are refused with 429. `0` disables the limit.", DefaultValue = "30")]
    int StateDownloadsPerMinutePerClient { get; set; }
}
