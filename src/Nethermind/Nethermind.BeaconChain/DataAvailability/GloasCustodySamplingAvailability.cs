// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

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
/// Cryptographic verification is cached per immutable sidecar and bid commitments. The first pending
/// candidate that verifies against the bid and names the bid's slot is moved into the served map, and
/// candidates that fail are discarded. Fails closed while the node's
/// identity is unknown. The window is read from <paramref name="clock"/> at every check.
/// </para>
/// </remarks>
public sealed class GloasCustodySamplingAvailability(INodeColumnCustodySource custodySource, DataColumnSidecarPool pool, SlotClock clock, BeaconChainSpec spec)
{
    private readonly ConditionalWeakTable<DataColumnSidecarGloas, SszKzgCommitment[]> _verified = [];

    /// <summary>Whether the blob data of <paramref name="bid"/>, committed by the block at <paramref name="blockRoot"/>, is available to this node.</summary>
    /// <param name="blockRoot">The <c>beacon_block_root</c> the envelope names.</param>
    /// <param name="bid">The bid that block committed; its <c>slot</c> is the block's slot (<c>process_execution_payload_bid</c>).</param>
    public bool IsDataAvailable(Hash256 blockRoot, ExecutionPayloadBid bid)
    {
        SszKzgCommitment[] blobCommitments = bid.BlobKzgCommitments ?? [];
        if (blobCommitments.Length == 0) return true;

        if (spec.GetEpoch(bid.Slot) < DataAvailabilityBoundary.Compute(clock.CurrentEpoch, spec)) return true;

        if (custodySource.Current is not { } custody) return false;

        List<DataColumnSidecarGloas>? uncheckedColumns = null;
        Dictionary<ulong, bool> verified = [];
        foreach (ulong column in custody.SampledColumns)
        {
            if (pool.TryGetGloas(blockRoot, column, out DataColumnSidecarGloas? held))
            {
                if (held.Slot != bid.Slot || held.Index != column) continue;
                if (IsCached(held, blobCommitments)) verified[column] = true;
                else if (DataColumnSidecarVerifier.VerifyStructure(held, blobCommitments))
                    (uncheckedColumns ??= []).Add(held);
            }
            else if (pool.GetPendingGloas(blockRoot, column).Length == 0) return false;
        }
        if (uncheckedColumns is { Count: > 1 }
            && DataColumnSidecarVerifier.VerifyColumnBatches(uncheckedColumns, blobCommitments, static c => new(c.Index, c.Column!, c.KzgProofs!)))
        {
            SszKzgCommitment[] snapshot = (SszKzgCommitment[])blobCommitments.Clone();
            foreach (DataColumnSidecarGloas sidecar in uncheckedColumns)
            {
                _verified.AddOrUpdate(sidecar, snapshot);
                verified[sidecar.Index] = true;
            }
        }

        // The custody and sample sets overlap; verify each column once per check, not once per set.
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

    private bool IsCached(DataColumnSidecarGloas sidecar, SszKzgCommitment[] commitments) =>
        _verified.TryGetValue(sidecar, out SszKzgCommitment[]? verified)
        && MemoryMarshal.AsBytes(verified.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(commitments.AsSpan()));

    private bool Verify(DataColumnSidecarGloas sidecar, SszKzgCommitment[] commitments)
    {
        if (IsCached(sidecar, commitments)) return true;
        if (!DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, commitments)) return false;
        _verified.AddOrUpdate(sidecar, (SszKzgCommitment[])commitments.Clone());
        return true;
    }

    private bool IsVerifiedColumn(Hash256 blockRoot, ulong blockSlot, ulong column, SszKzgCommitment[] blobCommitments)
    {
        if (pool.TryGetGloas(blockRoot, column, out DataColumnSidecarGloas? held)
            && held.Slot == blockSlot && held.Index == column && Verify(held, blobCommitments))
        {
            return true;
        }

        // KZG does not bind the slot; a pending sidecar arrived before its block, so gossip's sidecar.slot == block.slot check has not run.
        foreach (DataColumnSidecarGloas pending in pool.GetPendingGloas(blockRoot, column))
        {
            if (pending.Slot == blockSlot && pending.Index == column && Verify(pending, blobCommitments))
            {
                pool.AddGloas(pending);
                return true;
            }

            // The block root fixes the bid, so a candidate that fails against it can never verify.
            pool.DiscardPendingGloas(pending);
        }

        return false;
    }
}
