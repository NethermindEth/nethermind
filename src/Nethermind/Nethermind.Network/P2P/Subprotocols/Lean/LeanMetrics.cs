// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

internal enum LeanEvent
{
    Announced,
    AnnouncementReceived,
    FetchStarted,
    Reassembled,
    Validated,
    Invalid,
    LocalFailure,
    Expired,
    Published,
    Served,
    RequestExpired,
    Violation
}

internal static class LeanMetrics
{
    private static readonly string[] Events =
    [
        "announced", "announcement_received", "fetch_started", "reassembled", "validated", "invalid", "local_failure",
        "expired", "published", "served", "request_expired", "violation"
    ];

    public static void Record(LeanEvent leanEvent) =>
        Metrics.LeanObjectEvents.AddOrUpdate(Events[(int)leanEvent], 1, static (_, value) => value + 1);

    public static void ChunkReceived() => Interlocked.Increment(ref Metrics.LeanChunksReceived);

    public static void ChunkServed() => Interlocked.Increment(ref Metrics.LeanChunksServed);

    public static void Gauges(int peers, int stored, int assemblies, long incompleteBytes)
    {
        Metrics.LeanPeers = peers;
        Metrics.LeanStoredObjects = stored;
        Metrics.LeanAssemblies = assemblies;
        Metrics.LeanIncompleteBytes = incompleteBytes;
    }
}
