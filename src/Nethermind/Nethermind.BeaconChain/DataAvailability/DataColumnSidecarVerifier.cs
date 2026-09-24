// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using CkzgLib;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Fulu <c>verify_data_column_sidecar</c> / <c>verify_data_column_sidecar_kzg_proofs</c> /
/// <c>verify_data_column_sidecar_inclusion_proof</c> (p2p-interface.md). A sidecar whose cells are
/// internally consistent but whose commitments were never included in the claimed block is still an
/// attack, so both the KZG check and the inclusion-proof check are required before trusting a sidecar.
/// Also the Gloas <c>verify_data_column_sidecar</c> / <c>verify_data_column_sidecar_kzg_proofs</c>
/// (gloas/p2p-interface.md), which take the commitments from the block's committed bid instead.
/// </summary>
public static class DataColumnSidecarVerifier
{
    /// <summary>
    /// Structural validity: index range, non-empty, and column/commitments/proofs all the same length.
    /// </summary>
    /// <remarks>
    /// Does not bound <c>len(kzg_commitments)</c> by <c>max_blobs_per_block</c>: that bound moves at
    /// scheduled epochs without a fork version bump (blob-parameter-only forks), so it needs the epoch
    /// the sidecar claims and is checked separately by <see cref="VerifyBlobCount"/>.
    /// </remarks>
    public static bool VerifyStructure(DataColumnSidecar sidecar)
    {
        if (sidecar.Index >= (ulong)Eip7594DasConstants.NumberOfColumns)
        {
            return false;
        }

        if (sidecar.KzgCommitments is not { Length: > 0 } commitments)
        {
            return false;
        }

        return sidecar.Column is { } column
            && sidecar.KzgProofs is { } proofs
            && column.Length == commitments.Length
            && proofs.Length == commitments.Length;
    }

    /// <summary>
    /// Full verification of a sidecar received from a peer, claiming to belong to <paramref name="epoch"/>.
    /// This is the entry point callers want: the individual checks are exposed for testing and are not
    /// safe to use piecemeal.
    /// </summary>
    /// <remarks>
    /// Ordered cheapest-first so a hostile peer cannot make the node pay for a KZG batch by sending
    /// a sidecar that fails a structural, blob-count or merkle check.
    /// </remarks>
    public static bool Verify(DataColumnSidecar sidecar, BeaconChainSpec spec) =>
        VerifyStructure(sidecar) && VerifyBlobCount(sidecar, spec) && VerifyInclusionProof(sidecar) && VerifyKzgProofs(sidecar);

    /// <summary>
    /// Rejects a sidecar whose commitment count exceeds the <c>max_blobs_per_block</c> scheduled for
    /// the sidecar's own epoch. Blob-parameter-only forks change this bound at a scheduled epoch
    /// without a fork version bump, so a fixed constant would keep accepting sidecars that are only
    /// valid under a stale schedule.
    /// </summary>
    /// <remarks>
    /// The epoch comes from the sidecar's own block header, never from the caller: a bound evaluated
    /// at an epoch the caller picks is not a bound. Fails closed on a sidecar with no commitments or
    /// no header, rather than treating either as vacuously within bound.
    /// </remarks>
    public static bool VerifyBlobCount(DataColumnSidecar sidecar, BeaconChainSpec spec)
    {
        if (sidecar.KzgCommitments is not { Length: > 0 } commitments)
        {
            return false;
        }

        if (sidecar.SignedBlockHeader?.Message is not { } header)
        {
            return false;
        }

        ulong epoch = spec.GetEpoch(header.Slot);

        ulong maxBlobsPerBlock = spec.GetBlobParameters(epoch)?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra;
        return (ulong)commitments.Length <= maxBlobsPerBlock;
    }

    /// <summary>
    /// Batch-verifies every cell in <paramref name="sidecar"/> against its commitments and proofs.
    /// </summary>
    /// <remarks>
    /// Per spec, every cell in a single sidecar shares the same cell index: its own column index.
    /// Re-checks structure first: the arrays are peer-supplied and indexed in lockstep below, and a
    /// zero-length batch verifies vacuously true in the native call.
    /// </remarks>
    public static bool VerifyKzgProofs(DataColumnSidecar sidecar)
    {
        if (!VerifyStructure(sidecar))
        {
            return false;
        }

        return VerifyCellBatch(sidecar.Index, sidecar.KzgCommitments!, sidecar.Column!, sidecar.KzgProofs!);
    }

    /// <summary>
    /// Gloas <c>verify_data_column_sidecar</c>: index range, non-empty, and column, proofs and
    /// <paramref name="kzgCommitments"/> all the same length.
    /// </summary>
    /// <param name="sidecar">The sidecar under check.</param>
    /// <param name="kzgCommitments">The <c>blob_kzg_commitments</c> of the bid committed by the block at <c>sidecar.beacon_block_root</c>.</param>
    /// <remarks>
    /// There is no blob-count bound here: <c>process_execution_payload_bid</c> already bounds the bid's
    /// commitments by <c>max_blobs_per_block</c>, and the column must match their count exactly.
    /// </remarks>
    public static bool VerifyStructure(DataColumnSidecarGloas sidecar, SszKzgCommitment[] kzgCommitments) =>
        sidecar.Index < (ulong)Eip7594DasConstants.NumberOfColumns
        && sidecar.Column is { Length: > 0 } column
        && column.Length == kzgCommitments.Length
        && sidecar.KzgProofs is { } proofs
        && proofs.Length == column.Length;

    /// <summary>Gloas <c>verify_data_column_sidecar_kzg_proofs</c>: batch-verifies every cell against <paramref name="kzgCommitments"/> and the sidecar's proofs.</summary>
    /// <param name="sidecar">The sidecar under check.</param>
    /// <param name="kzgCommitments">The <c>blob_kzg_commitments</c> of the bid committed by the block at <c>sidecar.beacon_block_root</c>.</param>
    /// <remarks>
    /// Runs <see cref="VerifyStructure(DataColumnSidecarGloas, SszKzgCommitment[])"/> first, so a pass is the full pair
    /// gloas/fork-choice.md <c>is_data_available</c> requires: the arrays are indexed in lockstep and an empty batch verifies vacuously.
    /// </remarks>
    public static bool VerifyKzgProofs(DataColumnSidecarGloas sidecar, SszKzgCommitment[] kzgCommitments) =>
        VerifyStructure(sidecar, kzgCommitments)
        && VerifyCellBatch(sidecar.Index, kzgCommitments, sidecar.Column!, sidecar.KzgProofs!);

    private static bool VerifyCellBatch(ulong columnIndex, SszKzgCommitment[] commitments, SszBlobCell[] cells, SszKzgCommitment[] proofs)
    {
        int count = commitments.Length;

        using ArrayPoolSpan<byte> flatCommitments = new(count * Ckzg.BytesPerCommitment);
        using ArrayPoolSpan<byte> flatProofs = new(count * Ckzg.BytesPerProof);
        using ArrayPoolSpan<byte> flatCells = new(count * Ckzg.BytesPerCell);
        using ArrayPoolSpan<ulong> cellIndices = new(count);

        for (int i = 0; i < count; i++)
        {
            commitments[i].AsSpan().CopyTo(flatCommitments.Slice(i * Ckzg.BytesPerCommitment, Ckzg.BytesPerCommitment));
            proofs[i].AsSpan().CopyTo(flatProofs.Slice(i * Ckzg.BytesPerProof, Ckzg.BytesPerProof));
            cells[i].AsSpan().CopyTo(flatCells.Slice(i * Ckzg.BytesPerCell, Ckzg.BytesPerCell));
            // The column index also represents the cell index for every row in this sidecar.
            cellIndices[i] = columnIndex;
        }

        try
        {
            return Ckzg.VerifyCellKzgProofBatch(flatCommitments, cellIndices, flatCells, flatProofs, count, DasKzg.Handle);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies that <c>hash_tree_root(sidecar.kzg_commitments)</c> is included in the block whose
    /// header is <c>sidecar.signed_block_header</c>, via <c>kzg_commitments_inclusion_proof</c>.
    /// </summary>
    public static bool VerifyInclusionProof(DataColumnSidecar sidecar)
    {
        if (sidecar.SignedBlockHeader?.Message?.BodyRoot is not { } bodyRoot
            || sidecar.KzgCommitmentsInclusionProof is not { Length: Eip7594DasConstants.KzgCommitmentsInclusionProofDepth } branch
            || sidecar.KzgCommitments is not { } commitments)
        {
            return false;
        }

        Hash256 leaf = ComputeCommitmentsListRoot(commitments);
        return IsValidMerkleBranch(
            leaf.Bytes,
            branch,
            Eip7594DasConstants.KzgCommitmentsInclusionProofDepth,
            Eip7594DasConstants.BlobKzgCommitmentsSubtreeIndex,
            bodyRoot.Bytes);
    }

    /// <summary><c>hash_tree_root</c> of the <c>kzg_commitments</c> field alone (a List[KZGCommitment, LIMIT]).</summary>
    public static Hash256 ComputeCommitmentsListRoot(SszKzgCommitment[] commitments)
    {
        UInt256[] chunks = new UInt256[commitments.Length];
        for (int i = 0; i < commitments.Length; i++)
        {
            Merkle.Merkleize(out chunks[i], commitments[i].AsSpan());
        }

        Merkle.Merkleize(out UInt256 dataRoot, chunks, Eip7594DasConstants.MaxBlobCommitmentsPerBlock);
        Merkle.MixIn(ref dataRoot, commitments.Length);
        return new Hash256(dataRoot.ToLittleEndian());
    }

    /// <summary>
    /// Standard SSZ generalized-index merkle branch check: folds <paramref name="leaf"/> up through
    /// <paramref name="branch"/> using <paramref name="index"/>'s bits to pick left/right at each
    /// level, and compares the result to <paramref name="root"/>.
    /// </summary>
    private static bool IsValidMerkleBranch(ReadOnlySpan<byte> leaf, Hash256[] branch, int depth, int index, ReadOnlySpan<byte> root)
    {
        Span<byte> value = stackalloc byte[32];
        leaf.CopyTo(value);
        Span<byte> combined = stackalloc byte[64];

        for (int level = 0; level < depth; level++)
        {
            ReadOnlySpan<byte> sibling = branch[level].Bytes;
            if (((index >> level) & 1) == 1)
            {
                sibling.CopyTo(combined);
                value.CopyTo(combined[32..]);
            }
            else
            {
                value.CopyTo(combined);
                sibling.CopyTo(combined[32..]);
            }

            SHA256.HashData(combined, value);
        }

        return value.SequenceEqual(root);
    }
}
