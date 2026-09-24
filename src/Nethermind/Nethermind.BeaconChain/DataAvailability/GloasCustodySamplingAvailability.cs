// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Gloas <c>is_data_available(beacon_block_root)</c> (gloas/fork-choice.md) for a node that custodies
/// a fraction of the matrix: the block's payload is available once this node holds, for every column
/// it custodies and every column of its per-slot sample, a sidecar that passes
/// <c>verify_data_column_sidecar</c> and <c>verify_data_column_sidecar_kzg_proofs</c> against the
/// <c>blob_kzg_commitments</c> of the bid the block committed.
/// </summary>
/// <remarks>
/// <para>
/// Gloas checks availability for the envelope, not the block (gloas/fork-choice.md
/// <c>on_execution_payload_envelope</c>), so <see cref="IsDataAvailable"/> matches the
/// <see cref="ExecutionPayloadEnvelopeImporter"/> delegate rather than <see cref="IDataAvailabilityRule"/>.
/// Custody, sampling and the retention window are Fulu's (<see cref="CustodySamplingAvailability"/>):
/// Gloas changes neither das-core nor <c>MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS</c>.
/// </para>
/// <para>
/// Every sidecar is re-verified at every check, so a pool entry never counts on trust. A pending
/// sidecar that verifies against the bid and names the bid's slot is moved into the served map. Fails closed while the node's
/// identity is unknown. The window is read from <paramref name="clock"/> at every check.
/// </para>
/// </remarks>
public sealed class GloasCustodySamplingAvailability(INodeColumnCustodySource custodySource, DataColumnSidecarPool pool, SlotClock clock, BeaconChainSpec spec)
{
    /// <summary>Whether the blob data of <paramref name="bid"/>, committed by the block at <paramref name="blockRoot"/>, is available to this node.</summary>
    /// <param name="blockRoot">The <c>beacon_block_root</c> the envelope names.</param>
    /// <param name="bid">The bid that block committed; its <c>slot</c> is the block's slot (<c>process_execution_payload_bid</c>).</param>
    public bool IsDataAvailable(Hash256 blockRoot, ExecutionPayloadBid bid)
    {
        SszKzgCommitment[] blobCommitments = bid.BlobKzgCommitments ?? [];
        if (blobCommitments.Length == 0) return true;

        if (spec.GetEpoch(bid.Slot) < DataAvailabilityBoundary.Compute(clock.CurrentEpoch, spec)) return true;

        if (custodySource.Current is not { } custody) return false;

        // The custody and sample sets overlap; verify each column once per check, not once per set.
        Dictionary<ulong, bool> verified = [];
        bool IsHeldAndVerified(ulong column)
        {
            if (!verified.TryGetValue(column, out bool ok))
            {
                ok = IsVerifiedColumn(blockRoot, bid.Slot, column, blobCommitments);
                verified[column] = ok;
            }

            return ok;
        }

        // SampledColumns contains CustodyColumns, so this loop only fails fast before sampling.
        foreach (ulong column in custody.CustodyColumns)
        {
            if (!IsHeldAndVerified(column)) return false;
        }

        return DataAvailabilitySampling.IsSampleAvailable(custody.SampledColumns, IsHeldAndVerified);
    }

    private bool IsVerifiedColumn(Hash256 blockRoot, ulong blockSlot, ulong column, SszKzgCommitment[] blobCommitments)
    {
        if (pool.TryGetGloas(blockRoot, column, out DataColumnSidecarGloas? held)
            && DataColumnSidecarVerifier.VerifyKzgProofs(held, blobCommitments))
        {
            return true;
        }

        // KZG does not bind the slot; a pending sidecar arrived before its block, so gossip's sidecar.slot == block.slot check has not run.
        if (pool.TryGetPendingGloas(blockRoot, column, out DataColumnSidecarGloas? pending)
            && pending.Slot == blockSlot
            && DataColumnSidecarVerifier.VerifyKzgProofs(pending, blobCommitments))
        {
            pool.AddGloas(pending);
            return true;
        }

        return false;
    }
}
