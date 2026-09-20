// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The supernode <c>is_data_available</c>: the caller hands over the sidecars it retrieved for the
/// block, and every column in <c>[0, NumberOfColumns)</c> must be present exactly once, addressed to
/// this block, and independently verified. This is the rule the consensus-spec fork choice vectors
/// encode (their <c>columns</c> steps list the full matrix), and only that path should select it.
/// </summary>
/// <param name="dataColumns">
/// The retrieved sidecars; <c>null</c> or empty is only acceptable for a block with no blob commitments.
/// </param>
public sealed class FullColumnSetAvailability(IReadOnlyList<DataColumnSidecar>? dataColumns) : IDataAvailabilityRule
{
    /// <inheritdoc/>
    /// <remarks>
    /// Every check is required to fail closed: a sidecar count match alone does not prove availability
    /// if a sidecar were addressed to a different block, claimed the wrong commitments, or failed its
    /// own KZG or inclusion proof.
    /// </remarks>
    public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec)
    {
        SszKzgCommitment[] blobCommitments = block.Body?.BlobKzgCommitments ?? [];
        if (blobCommitments.Length == 0) return true;

        if (!DataColumnAvailability.HasExactlyOneSidecarPerColumn(dataColumns)) return false;

        foreach (DataColumnSidecar sidecar in dataColumns!)
        {
            if (!DataColumnAvailability.IsVerifiedColumnOf(sidecar, blockRoot, blobCommitments, spec)) return false;
        }

        return true;
    }
}
