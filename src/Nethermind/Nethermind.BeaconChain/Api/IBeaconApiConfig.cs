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
    [ConfigItem(Description = "Whether to serve the Beacon API (REST/SSE) surface. Requires BeaconChain.Enabled.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The host the Beacon API Kestrel listener binds to.", DefaultValue = "\"127.0.0.1\"")]
    string Host { get; set; }

    [ConfigItem(Description = "The port the Beacon API listens on.", DefaultValue = "5052")]
    int Port { get; set; }

    [ConfigItem(Description = "The maximum concurrent Beacon API state requests in each of two independent pools: downloads (/eth/v2/debug/beacon/states and the registry-sized /eth/v1/beacon/states routes validators, validator_balances, validator_identities, committees and sync_committees, plus non-head validators/{validator_id}, pending_deposits, pending_partial_withdrawals, pending_consolidations and proposer_lookahead) and other /eth/v1/beacon/states queries, which exclude root and fork. Each pool refuses excess requests with 503 without queuing; per-client limits return 429.", DefaultValue = "2")]
    int MaxConcurrentStateRequests { get; set; }

    [ConfigItem(Description = "The maximum number of downloads (as counted by `MaxConcurrentStateRequests`) per minute from one client address (an IPv6 client is counted by its /64 prefix). Requests beyond it are refused with 429. `0` disables the limit.", DefaultValue = "30")]
    int StateDownloadsPerMinutePerClient { get; set; }

    [ConfigItem(Description = "The maximum number of /eth/v1/beacon/states queries other than downloads, root and fork (as counted by `MaxConcurrentStateRequests`) per minute from one client address (an IPv6 client is counted by its /64 prefix). Requests beyond it are refused with 429. `0` disables the limit.", DefaultValue = "600")]
    int StateQueriesPerMinutePerClient { get; set; }

    [ConfigItem(Description = "The maximum number of state requests (as counted by `MaxConcurrentStateRequests`) one client address may have in flight at once; an IPv6 client is counted by its /64 prefix, and loopback is limited like any other address. Raise it for tooling or a proxy that fronts several clients from one address. Requests beyond it are refused with 429. Must be at least 1.", DefaultValue = "1")]
    int MaxConcurrentStateRequestsPerClient { get; set; }

    [ConfigItem(Description = "The most time, in seconds, one state request may take in total, however steadily its response is written. When it passes, the response is aborted and the request's permits are released. Set it generously: a slow but steady download of a large state must finish inside it. Must be between 1 and 4294967.", DefaultValue = "3600")]
    int StateResponseTimeoutSeconds { get; set; }

    [ConfigItem(Description = "The maximum idle time in seconds between response writes. POST requests are also bounded from admission through their first write. GET preparation is bounded by StateResponseTimeoutSeconds. HTTP/1.x responses additionally require 64 KiB/s after a ten-second grace period. Expiry aborts the request and releases its permits. Must be between 1 and 4294967 and large enough for one 64 KiB chunk at the total timeout's minimum throughput.", DefaultValue = "120")]
    int StateResponseIdleTimeoutSeconds { get; set; }
}
