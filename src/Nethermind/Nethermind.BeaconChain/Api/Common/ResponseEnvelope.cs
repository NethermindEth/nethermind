// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Derives the beacon-api response envelope flags and the <c>Eth-Consensus-Version</c> header.</summary>
internal static class ResponseEnvelope
{
    private static readonly ConditionalWeakTable<ForkChoiceSnapshot, Lazy<Dictionary<Hash256, ExecutionStatus>>> ExecutionStatuses = [];
    public const string ConsensusVersionHeader = "Eth-Consensus-Version";

    public static string ForkName(BeaconFork fork) => fork switch
    {
        BeaconFork.Electra => "electra",
        BeaconFork.Fulu => "fulu",
        BeaconFork.Gloas => "gloas",
        _ => throw new ArgumentOutOfRangeException(nameof(fork), fork, null),
    };

    /// <summary>
    /// True while the current head has not been confirmed VALID by the execution layer.
    /// </summary>
    /// <remarks>
    /// Reads the flag <see cref="Sync.BeaconSyncOrchestrator"/> itself sets after a
    /// forkchoiceUpdated verdict, rather than a value computed independently, so this can never
    /// disagree with what the driver believes about its own head. Before the first head step the
    /// flag is false, which reports optimistic=true: nothing has been confirmed yet.
    /// </remarks>
    public static bool ExecutionOptimistic(IBeaconChainStatusSource statusSource) => !statusSource.ExecutionInSync;

    /// <summary>Whether the referenced payload is unverified (Beacon API types/primitive.yaml ExecutionOptimistic).</summary>
    public static bool ExecutionOptimistic(BeaconApiContext ctx, Hash256 root)
    {
        if (ctx.ForkChoiceSnapshot is { } snapshot)
        {
            Dictionary<Hash256, ExecutionStatus> statuses = ExecutionStatuses.GetValue(snapshot,
                static current => new Lazy<Dictionary<Hash256, ExecutionStatus>>(() => IndexExecutionStatuses(current))).Value;
            if (statuses.TryGetValue(root, out ExecutionStatus status)) return status == ExecutionStatus.Optimistic;

            // types/primitive.yaml ExecutionOptimistic: only a verified checkpoint verifies its canonical ancestors.
            if (statuses.TryGetValue(snapshot.FinalizedCheckpoint.Root, out ExecutionStatus finalizedStatus)
                && finalizedStatus is ExecutionStatus.Valid or ExecutionStatus.Irrelevant
                && ctx.Store.TryGetBlockSlot(root, out ulong slot)
                && slot <= snapshot.FinalizedCheckpoint.Epoch * ctx.Spec.SlotsPerEpoch
                && IsFinalized(ctx, slot, root))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<Hash256, ExecutionStatus> IndexExecutionStatuses(ForkChoiceSnapshot snapshot)
    {
        Dictionary<Hash256, ExecutionStatus> statuses = new(snapshot.Nodes.Count);
        foreach (ForkChoiceSnapshotNode node in snapshot.Nodes) statuses[node.Root] = node.ExecutionStatus;
        return statuses;
    }

    /// <summary>Whether the block is canonical at or before the finalized checkpoint's start slot (Beacon API types/primitive.yaml Finalized).</summary>
    /// <remarks>A non-canonical block at a finalized epoch is exactly what finalization discarded, so the epoch alone must not vouch for it.</remarks>
    public static bool IsFinalized(BeaconApiContext ctx, ulong slot, Hash256 root) =>
        slot <= ctx.StatusSource.CurrentStatus.FinalizedEpoch * ctx.Spec.SlotsPerEpoch
        && ctx.Store.TryGetCanonicalRoot(slot, out Hash256? canonicalRoot)
        && canonicalRoot == root;

    /// <summary>Whether the state stored under <paramref name="root"/> is part of finalized history.</summary>
    /// <remarks>
    /// A state is keyed by the root of the block it came from, but slot processing can advance it past
    /// that block, so its own <c>Slot</c> may name an empty slot or one filled by a different block.
    /// The canonical lookup must use the slot of the block the state is keyed by.
    /// </remarks>
    public static bool IsFinalized(BeaconApiContext ctx, ApiState state, Hash256 root) =>
        state.Slot <= ctx.StatusSource.CurrentStatus.FinalizedEpoch * ctx.Spec.SlotsPerEpoch
        && IsFinalized(ctx, state.LatestBlockHeader.Slot, root);

    public static void ApplyConsensusVersionHeader(HttpContext ctx, BeaconChainSpec spec, ulong slot) =>
        ctx.Response.Headers[ConsensusVersionHeader] = ForkName(spec.ForkAtEpoch(spec.GetEpoch(slot)));
}
