// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The earliest epoch whose data column sidecars the network still guarantees to serve, the Fulu
/// p2p spec's <c>max(current_epoch - MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS, FULU_FORK_EPOCH)</c>.
/// One definition for the import gate and the req/resp serve ranges, so no two callers can disagree.
/// </summary>
/// <remarks>
/// <c>current_epoch</c> is the wall-clock epoch (DataColumnSidecarsByRange: "where <c>current_epoch</c>
/// is defined by the current wall-clock time"), never the head or the finalized epoch. Keyed to the
/// head, a lagging node's window would lag with it and admit window-age blocks without their data;
/// keyed to finality, a checkpoint-synced node would demand columns nobody serves. The window moves
/// every epoch, so callers recompute it at every check rather than caching the result.
/// </remarks>
public static class DataAvailabilityBoundary
{
    /// <summary><c>MIN_EPOCHS_FOR_BLOB_SIDECARS_REQUESTS</c> from deneb/p2p-interface.md, <c>2**12</c> in configs/mainnet.yaml.</summary>
    public static ulong MinEpochsForBlobSidecarsRequests => Presets.IsGnosis ? 16384UL : 4096UL;

    /// <summary>The first epoch inside the retention window as of <paramref name="currentEpoch"/>; blocks in earlier epochs need no columns.</summary>
    public static ulong Compute(ulong currentEpoch, BeaconChainSpec spec)
    {
        ulong windowStart = currentEpoch >= Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests
            ? currentEpoch - Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests
            : 0;
        return Math.Max(windowStart, spec.FuluForkEpoch);
    }

    /// <summary>Whether a pre-Fulu block of <paramref name="blockEpoch"/> is inside the blob sidecar retention window as of <paramref name="currentEpoch"/>.</summary>
    /// <remarks>
    /// deneb/p2p-interface.md bounds that window below by <c>max(current_epoch - MIN_EPOCHS_FOR_BLOB_SIDECARS_REQUESTS, DENEB_FORK_EPOCH)</c>;
    /// every block this driver can represent is at or past Electra, so the Deneb fork term never binds.
    /// </remarks>
    public static bool IsInBlobSidecarWindow(ulong blockEpoch, ulong currentEpoch) =>
        currentEpoch < MinEpochsForBlobSidecarsRequests || blockEpoch >= currentEpoch - MinEpochsForBlobSidecarsRequests;

    /// <summary>The first slot of <c>data_column_serve_range</c> as of <paramref name="currentEpoch"/>: the start slot of <see cref="Compute"/>'s epoch.</summary>
    public static ulong ComputeStartSlot(ulong currentEpoch, BeaconChainSpec spec)
    {
        ulong epoch = Compute(currentEpoch, spec);
        return epoch > ulong.MaxValue / spec.SlotsPerEpoch ? ulong.MaxValue : epoch * spec.SlotsPerEpoch;
    }
}
