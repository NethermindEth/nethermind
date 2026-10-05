// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;

namespace Nethermind.BeaconChain;

/// <summary>Label key for the gossip-rejection metric: topic name plus <see cref="GossipDropReason"/>.</summary>
public readonly record struct GossipRejectKey(string Topic, GossipDropReason Reason) : IMetricLabels
{
    public string[] Labels => [Topic, Reason.ToString()];
}

public class Metrics
{
    internal static ulong PeersConnectedCount;
    internal static ulong ChannelsReopenedCount;
    internal static ulong PeersDroppedCount;
    internal static ulong GossipAcceptedCount;
    internal static ulong GossipDroppedCount;
    internal static ulong GossipForwardedCount;
    internal static ulong GossipThrottledCount;
    internal static ulong GossipAbandonedVerdictCount;
    internal static ulong GossipLateVerdictCount;
    internal static ulong BlocksImportedCount;
    internal static ulong DialAttemptsCount;
    internal static ulong HeldBlocksDroppedCount;
    internal static ulong NewPayloadCallsCount;
    internal static ulong NewPayloadMillisecondsCount;
    internal static ulong ForkchoiceUpdatedCallsCount;
    private static long _headSlotDelay;
    private static int _peerCount;
    private static long _lastBlockImportMs;

    [GaugeMetric]
    [Description("Head slot of the embedded beacon chain driver.")]
    public static ulong BeaconChainHeadSlot { get; set; }

    [GaugeMetric]
    [Description("Slots between the wall clock and the embedded driver's head.")]
    public static long BeaconChainHeadSlotDelay { get => Interlocked.Read(ref _headSlotDelay); set => Interlocked.Exchange(ref _headSlotDelay, value); }

    [GaugeMetric]
    [Description("Finalized epoch tracked by the embedded beacon chain driver.")]
    public static ulong BeaconChainFinalizedEpoch { get; set; }

    [GaugeMetric]
    [Description("Justified epoch tracked by the embedded beacon chain driver.")]
    public static ulong BeaconChainJustifiedEpoch { get; set; }

    [GaugeMetric]
    [Description("Whether the execution layer is in sync with the embedded driver's head (1) or still syncing (0).")]
    public static int BeaconChainElInSync { get; set; }

    [GaugeMetric]
    [Description("Connected, status-exchanged beacon chain peers.")]
    public static int BeaconChainPeerCount { get => Volatile.Read(ref _peerCount); set => Volatile.Write(ref _peerCount, value); }

    [CounterMetric]
    [Description("Requests and identify dials whose first channel never reached its protocol and were opened once more.")]
    public static ulong BeaconChainChannelsReopened { get => Volatile.Read(ref ChannelsReopenedCount); set => Volatile.Write(ref ChannelsReopenedCount, value); }

    [CounterMetric]
    [Description("Beacon chain peer connections established.")]
    public static ulong BeaconChainPeersConnected { get => Volatile.Read(ref PeersConnectedCount); set => Volatile.Write(ref PeersConnectedCount, value); }

    [CounterMetric]
    [Description("Beacon chain peers dropped.")]
    public static ulong BeaconChainPeersDropped { get => Volatile.Read(ref PeersDroppedCount); set => Volatile.Write(ref PeersDroppedCount, value); }

    [KeyIsLabel("reason")]
    [Description("Beacon chain peers dropped, by goodbye reason.")]
    public static ConcurrentDictionary<StringLabel, long> BeaconChainPeersDroppedByReason { get; } = new();

    [KeyIsLabel("reason")]
    [Description("Beacon chain peer failures reported by range-sync/backfill callers, by closed-cardinality reason.")]
    public static ConcurrentDictionary<StringLabel, long> BeaconChainPeerFailuresByReason { get; } = new();

    [CounterMetric]
    [Description("Outbound dials attempted toward discovered beacon chain peers.")]
    public static ulong BeaconChainDialAttempts { get => Volatile.Read(ref DialAttemptsCount); set => Volatile.Write(ref DialAttemptsCount, value); }

    [CounterMetric]
    [Description("Beacon blocks imported through the state transition.")]
    public static ulong BeaconChainBlocksImported { get => Volatile.Read(ref BlocksImportedCount); set => Volatile.Write(ref BlocksImportedCount, value); }

    [GaugeMetric]
    [Description("Milliseconds spent importing the most recent beacon block.")]
    public static long BeaconChainLastBlockImportMs { get => Interlocked.Read(ref _lastBlockImportMs); set => Interlocked.Exchange(ref _lastBlockImportMs, value); }

    [CounterMetric]
    [Description("Blocks held for a parent or a deferred block that were dropped because it could not import.")]
    public static ulong BeaconChainHeldBlocksDropped { get => Volatile.Read(ref HeldBlocksDroppedCount); set => Volatile.Write(ref HeldBlocksDroppedCount, value); }

    [CounterMetric]
    [Description("Gossip messages accepted across the beacon chain topics.")]
    public static ulong BeaconChainGossipAccepted { get => Volatile.Read(ref GossipAcceptedCount); set => Volatile.Write(ref GossipAcceptedCount, value); }

    [CounterMetric]
    [Description("Gossip messages dropped during validation.")]
    public static ulong BeaconChainGossipDropped { get => Volatile.Read(ref GossipDroppedCount); set => Volatile.Write(ref GossipDroppedCount, value); }

    [CounterMetric]
    [Description("Deferred gossip messages the pubsub router forwarded once their validation accepted them.")]
    public static ulong BeaconChainGossipForwarded { get => Volatile.Read(ref GossipForwardedCount); set => Volatile.Write(ref GossipForwardedCount, value); }

    [CounterMetric]
    [Description("Gossip messages refused for validation because the messages or bytes awaiting a verdict reached their bound.")]
    public static ulong BeaconChainGossipThrottled { get => Volatile.Read(ref GossipThrottledCount); set => Volatile.Write(ref GossipThrottledCount, value); }

    [CounterMetric]
    [Description("Deferred gossip messages whose verdict was not given before the pending validation timeout or shutdown.")]
    public static ulong BeaconChainGossipVerdictsAbandoned { get => Volatile.Read(ref GossipAbandonedVerdictCount); set => Volatile.Write(ref GossipAbandonedVerdictCount, value); }

    [CounterMetric]
    [Description("Gossip verdicts given after the message was abandoned or expired from the router, so neither forwarded nor charged to its sender.")]
    public static ulong BeaconChainGossipVerdictsLate { get => Volatile.Read(ref GossipLateVerdictCount); set => Volatile.Write(ref GossipLateVerdictCount, value); }

    [KeyIsLabel("reason")]
    [Description("Data column gossip messages dropped, by validation reason.")]
    public static ConcurrentDictionary<StringLabel, long> BeaconChainColumnGossipDroppedByReason { get; } = new();

    [KeyIsLabel("topic")]
    [Description("Gossip messages received per beacon chain topic, before validation.")]
    public static ConcurrentDictionary<StringLabel, long> BeaconChainGossipReceivedByTopic { get; } = new();

    [KeyIsLabel("topic", "reason")]
    [Description("Gossip messages dropped during decode-level validation, per topic and drop reason.")]
    public static ConcurrentDictionary<GossipRejectKey, long> BeaconChainGossipRejectedByTopic { get; } = new();

    [KeyIsLabel("protocol_id", "reason")]
    [Description("Eth2 req/resp failures per protocol id and reason: timeouts, peer-attributable invalid messages, limit violations (chunk cap, concurrent-request cap), and peer error responses.")]
    public static ConcurrentDictionary<ReqRespFailureKey, long> BeaconChainReqRespFailures { get; } = new();

    [KeyIsLabel("operation")]
    [Description("Operations refused by fork choice, by source: attestations and attester slashings the state transition or gossip validation accepted, from block bodies (tolerated, the block still imports) or gossip (dropped); and execution payload envelopes that failed verification or that the execution layer found invalid.")]
    public static ConcurrentDictionary<StringLabel, long> BeaconChainForkChoiceRejections { get; } = new();

    [CounterMetric]
    [Description("In-process engine_newPayload calls issued by the embedded driver.")]
    public static ulong BeaconChainNewPayloadCalls { get => Volatile.Read(ref NewPayloadCallsCount); set => Volatile.Write(ref NewPayloadCallsCount, value); }

    [CounterMetric]
    [Description("Milliseconds the embedded driver spent waiting on in-process engine_newPayload calls.")]
    public static ulong BeaconChainNewPayloadMilliseconds { get => Volatile.Read(ref NewPayloadMillisecondsCount); set => Volatile.Write(ref NewPayloadMillisecondsCount, value); }

    [CounterMetric]
    [Description("In-process engine_forkchoiceUpdated calls issued by the embedded driver.")]
    public static ulong BeaconChainForkchoiceUpdatedCalls { get => Volatile.Read(ref ForkchoiceUpdatedCallsCount); set => Volatile.Write(ref ForkchoiceUpdatedCallsCount, value); }
}
