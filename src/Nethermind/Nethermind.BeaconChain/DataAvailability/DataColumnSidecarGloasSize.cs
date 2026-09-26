// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Gloas <c>compute_max_data_column_sidecar_size</c> (gloas/p2p-interface.md): the type-specific SSZ
/// bound for a serialized <see cref="DataColumnSidecarGloas"/>.
/// </summary>
public static class DataColumnSidecarGloasSize
{
    /// <summary>
    /// The serialized size of <see cref="DataColumnSidecarGloas"/> without its two lists: <c>index</c>,
    /// the <c>column</c> and <c>kzg_proofs</c> offsets, <c>slot</c> and <c>beacon_block_root</c>.
    /// </summary>
    public const int FixedPartLength = sizeof(ulong) + sizeof(uint) + sizeof(uint) + sizeof(ulong) + 32;

    /// <summary>The serialized size one blob adds to a sidecar: one cell and one cell KZG proof.</summary>
    public const int BytesPerBlob = SszBlobCell.BlobCellLength + SszKzgCommitment.KzgCommitmentLength;

    /// <summary>
    /// The size of a sidecar serialized with the largest <c>max_blobs_per_block</c> in
    /// <paramref name="spec"/>: <see cref="BeaconChainSpec.MaxBlobsPerBlockElectra"/> and every
    /// <see cref="BeaconChainSpec.BlobSchedule"/> entry.
    /// </summary>
    /// <remarks>
    /// Every entry counts, whatever its epoch, because the spec takes the maximum over the whole schedule
    /// "regardless of whether or not that is the current max_blobs_per_block". A progressive list of
    /// fixed-size elements serializes as its elements alone, so the size is linear in the blob count.
    /// </remarks>
    /// <exception cref="OverflowException">The schedule's largest blob count does not fit the size in a <see cref="ulong"/>.</exception>
    public static ulong ComputeMax(BeaconChainSpec spec)
    {
        ulong maxBlobs = spec.MaxBlobsPerBlockElectra;
        foreach (BlobScheduleEntry entry in spec.BlobSchedule)
        {
            maxBlobs = Math.Max(maxBlobs, entry.MaxBlobsPerBlock);
        }

        return checked(FixedPartLength + (maxBlobs * BytesPerBlob));
    }
}
