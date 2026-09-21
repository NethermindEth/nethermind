// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// The production <c>is_data_available</c> for a node that custodies a fraction of the matrix: the
/// block is available once this node holds a verified sidecar for every column it custodies AND
/// custody sampling succeeded, i.e. every column of its per-slot sample is held and verified
/// (das-core.md: "Sampling is considered successful if the node manages to retrieve all selected
/// columns"). The sample already contains the custody columns by construction; both are still
/// checked by name so the rule does not silently rest on that derivation staying true. Blocks
/// older than the <see cref="DataAvailabilityBoundary"/> are available without any column: the
/// network no longer guarantees to serve them (fork-choice.md <c>is_data_available</c>).
/// </summary>
/// <remarks>
/// Every sidecar is re-verified here rather than trusted because it sits in <paramref name="columns"/>:
/// the gossip router validates a sidecar against its own header, not against the block being
/// imported, and a future source may be fed from unverified req/resp fetches. Fails closed while the
/// node's identity is unknown (<see cref="INodeColumnCustodySource.Current"/> is <c>null</c>), since a
/// rule that cannot say which columns it needs cannot say a block is available either. The window
/// is read from <paramref name="clock"/> at every check because it moves with the wall clock.
/// </remarks>
public sealed class CustodySamplingAvailability(INodeColumnCustodySource custodySource, IDataColumnSource columns, SlotClock clock) : IDataAvailabilityRule
{
    /// <inheritdoc/>
    public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec)
    {
        SszKzgCommitment[] blobCommitments = block.Body?.BlobKzgCommitments ?? [];
        if (blobCommitments.Length == 0) return true;

        if (spec.GetEpoch(block.Slot) < DataAvailabilityBoundary.Compute(clock.CurrentEpoch, spec)) return true;

        if (custodySource.Current is not { } custody) return false;

        // The custody and sample sets overlap; verify each column once per block, not once per set.
        Dictionary<ulong, bool> verified = [];
        bool IsHeldAndVerified(ulong column)
        {
            if (!verified.TryGetValue(column, out bool ok))
            {
                ok = columns.TryGetColumn(blockRoot, column, out DataColumnSidecar? sidecar)
                    && DataColumnAvailability.IsVerifiedColumnOf(sidecar, blockRoot, blobCommitments, spec);
                verified[column] = ok;
            }

            return ok;
        }

        foreach (ulong column in custody.CustodyColumns)
        {
            if (!IsHeldAndVerified(column)) return false;
        }

        return DataAvailabilitySampling.IsSampleAvailable(custody.SampledColumns, IsHeldAndVerified);
    }
}
