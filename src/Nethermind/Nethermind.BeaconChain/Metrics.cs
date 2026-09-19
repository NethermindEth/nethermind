// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.BeaconChain;

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

    [CounterMetric]
    [Description("In-process engine_newPayload calls issued by the embedded driver.")]
    public static ulong BeaconChainNewPayloadCalls { get; set; }

    [CounterMetric]
    [Description("In-process engine_forkchoiceUpdated calls issued by the embedded driver.")]
    public static ulong BeaconChainForkchoiceUpdatedCalls { get; set; }
}
