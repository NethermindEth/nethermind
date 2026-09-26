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
    [GaugeMetric]
    [Description("Head slot of the embedded beacon chain driver.")]
    public static ulong BeaconChainHeadSlot { get; set; }

    [GaugeMetric]
    [Description("Slots between the wall clock and the embedded driver's head.")]
    public static long BeaconChainHeadSlotDelay { get; set; }

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
    public static int BeaconChainPeerCount { get; set; }

    [CounterMetric]
    [Description("Beacon chain peer connections established.")]
    public static ulong BeaconChainPeersConnected { get; set; }

    [CounterMetric]
    [Description("Beacon chain peers dropped.")]
    public static ulong BeaconChainPeersDropped { get; set; }

    [KeyIsLabel("reason")]
    [Description("Beacon chain peers dropped, by goodbye reason.")]
    public static ConcurrentDictionary<StringLabel, long> BeaconChainPeersDroppedByReason { get; } = new();

    [KeyIsLabel("reason")]
    [Description("Beacon chain peer failures reported by range-sync/backfill callers, by closed-cardinality reason.")]
    public static ConcurrentDictionary<StringLabel, long> BeaconChainPeerFailuresByReason { get; } = new();

    [CounterMetric]
    [Description("Outbound dials attempted toward discovered beacon chain peers.")]
    public static ulong BeaconChainDialAttempts { get; set; }

    [CounterMetric]
    [Description("Beacon blocks imported through the state transition.")]
    public static ulong BeaconChainBlocksImported { get; set; }

    [GaugeMetric]
    [Description("Milliseconds spent importing the most recent beacon block.")]
    public static long BeaconChainLastBlockImportMs { get; set; }

    [CounterMetric]
    [Description("Gossip messages accepted across the beacon chain topics.")]
    public static ulong BeaconChainGossipAccepted { get; set; }

    [CounterMetric]
    [Description("Gossip messages dropped during decode-level validation.")]
    public static ulong BeaconChainGossipDropped { get; set; }

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
    public static ulong BeaconChainNewPayloadCalls { get; set; }

    [CounterMetric]
    [Description("In-process engine_forkchoiceUpdated calls issued by the embedded driver.")]
    public static ulong BeaconChainForkchoiceUpdatedCalls { get; set; }
}
