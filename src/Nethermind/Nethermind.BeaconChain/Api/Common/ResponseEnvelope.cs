// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Derives the beacon-api response envelope flags and the <c>Eth-Consensus-Version</c> header.</summary>
internal static class ResponseEnvelope
{
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
    /// Reads <see cref="Metrics.BeaconChainElInSync"/> - the same flag
    /// <see cref="Sync.BeaconSyncOrchestrator"/> itself flips after a forkchoiceUpdated verdict,
    /// rather than a value computed independently, so this can never disagree with what the driver
    /// believes about its own head. Before the driver has completed its first head step the gauge
    /// defaults to 0, which reports optimistic=true: nothing has been confirmed yet, and that is the
    /// safe direction to be wrong in.
    /// </remarks>
    public static bool ExecutionOptimistic() => Metrics.BeaconChainElInSync == 0;

    /// <summary>Whether the block <paramref name="root"/> at <paramref name="slot"/> is part of finalized history: at or before the finalized checkpoint's epoch and canonical at its slot.</summary>
    /// <remarks>A non-canonical block at a finalized epoch is exactly what finalization discarded, so the epoch alone must not vouch for it.</remarks>
    public static bool IsFinalized(BeaconApiContext ctx, ulong slot, Hash256 root) =>
        ctx.Spec.GetEpoch(slot) <= ctx.StatusSource.CurrentStatus.FinalizedEpoch
        && ctx.Store.TryGetCanonicalRoot(slot, out Hash256? canonicalRoot)
        && canonicalRoot == root;

    public static void ApplyConsensusVersionHeader(HttpContext ctx, BeaconChainSpec spec, ulong slot) =>
        ctx.Response.Headers[ConsensusVersionHeader] = ForkName(spec.ForkAtEpoch(spec.GetEpoch(slot)));
}
