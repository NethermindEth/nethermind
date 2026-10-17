// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

internal enum WrapperEvent
{
    Announced,
    AnnouncementReceived,
    Requested,
    Received,
    Validated,
    Invalid,
    LocalFailure,
    Expired,
    Published,
    Served,
    RequestExpired,
    Violation,
    UpgradeDropped,
    GraceRefused,
    EnvelopeRequested,
    EnvelopeRecovered
}

internal static class WrapperMetrics
{
    private static readonly string[] Events =
    [
        "announced", "announcement_received", "requested", "received", "validated", "invalid", "local_failure",
        "expired", "published", "served", "request_expired", "violation", "upgrade_dropped", "grace_refused",
        "envelope_requested", "envelope_recovered"
    ];

    public static void Record(WrapperEvent wrapperEvent, long count = 1) =>
        Metrics.ProofWrapperEvents.AddOrUpdate(Events[(int)wrapperEvent], count, (_, value) => value + count);

    public static void Gauges(int peers, int stored, int fetches, long chargedBytes)
    {
        Metrics.ProofWrapperPeers = peers;
        Metrics.ProofWrapperStoredObjects = stored;
        Metrics.ProofWrapperFetches = fetches;
        Metrics.ProofWrapperChargedBytes = chargedBytes;
    }
}
