// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Nethermind.Core.Metric;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public readonly record struct LeanObjectEventKey(string Kind, string Event) : IMetricLabels
{
    public string[] Labels => [Kind, Event];
}

public readonly record struct LeanStreamBytesKey(string Direction, string Stream) : IMetricLabels
{
    public string[] Labels => [Direction, Stream];
}

/// <summary>A cached <c>kind</c> and <c>path</c> label pair of <see cref="Metrics.LeanObjectTransferMicros"/>.</summary>
internal sealed class LeanTransferLabels(string kind, string path) : IStableMetricLabels
{
    public string[] Labels { get; } = [kind, path];
}

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
    Violation,
    SidecarFetched,
    SidecarUnavailable
}

internal static class LeanMetrics
{
    private static readonly string[] Kinds = ["0", "1", "2", "3"];

    private static readonly string[] Events =
    [
        "announced", "announcement_received", "fetch_started", "reassembled", "validated", "invalid", "local_failure",
        "expired", "published", "served", "request_expired", "violation", "sidecar_fetched", "sidecar_unavailable"
    ];

    public static void Record(byte kind, LeanEvent leanEvent) =>
        Metrics.LeanObjectEvents.AddOrUpdate(new LeanObjectEventKey(Kinds[kind & 3], Events[(int)leanEvent]), 1, static (_, value) => value + 1);

    private static readonly LeanTransferLabels[] TransferLabels =
        [.. Kinds.SelectMany(static kind => new[] { new LeanTransferLabels(kind, "retrieval"), new LeanTransferLabels(kind, "broadcast") })];

    private static readonly NonBlocking.ConcurrentDictionary<byte, (LeanStreamBytesKey In, LeanStreamBytesKey Out)> StreamKeys = new();

    /// <summary>Records the time from an object's first announcement, accepted manifest or request to its validation.</summary>
    public static void Transferred(byte kind, bool broadcast, TimeSpan elapsed) =>
        Metrics.LeanObjectTransferMicros.Observe(elapsed.TotalMicroseconds, TransferLabels[(kind & 3) * 2 + (broadcast ? 1 : 0)]);

    /// <summary>Counts ethp2p stream bytes under the stream type its selector byte identifies.</summary>
    public static void Ethp2pBytes(bool outbound, byte selector, long bytes)
    {
        (LeanStreamBytesKey incoming, LeanStreamBytesKey outgoing) = StreamKeys.GetOrAdd(selector, static selector =>
        {
            string stream = selector switch
            {
                Ethp2p.LeanEthp2pProtocol.BcastStream => "bcast",
                Ethp2p.LeanEthp2pProtocol.SessStream => "sess",
                Ethp2p.LeanEthp2pProtocol.ChunkStream => "chunk",
                Ethp2p.LeanEthp2pProtocol.ControlStream => "control",
                Ethp2p.LeanEthp2pProtocol.ResponseStream => "response",
                _ => "other"
            };
            return (new LeanStreamBytesKey("in", stream), new LeanStreamBytesKey("out", stream));
        });
        Metrics.LeanEthp2pStreamBytes.AddOrUpdate(outbound ? outgoing : incoming, static (_, added) => added, static (_, value, added) => value + added, bytes);
    }

    /// <summary>Accumulates time spent in broadcast erasure coding.</summary>
    public static void Coding(TimeSpan elapsed) => Interlocked.Add(ref Metrics.LeanBroadcastCodingMicros, (long)elapsed.TotalMicroseconds);

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
