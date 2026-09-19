// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using CkzgLib;
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
/// </summary>
public static class DataColumnSidecarVerifier
{
    /// <summary>
    /// Structural validity: index range, non-empty, and column/commitments/proofs all the same length.
    /// </summary>
    /// <remarks>
    /// The spec also bounds <c>len(kzg_commitments)</c> by the current epoch's
    /// <c>max_blobs_per_block</c> fork parameter; that check needs the fork schedule (Spec/), which
    /// this PeerDAS foundation deliberately does not touch, and is left to the caller.
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
    /// Full verification of a sidecar received from a peer. This is the entry point callers want:
    /// the individual checks are exposed for testing and are not safe to use piecemeal.
    /// </summary>
    /// <remarks>
    /// Ordered cheapest-first so a hostile peer cannot make the node pay for a KZG batch by sending
    /// a sidecar that fails a structural or merkle check.
    /// </remarks>
    public static bool Verify(DataColumnSidecar sidecar) =>
        VerifyStructure(sidecar) && VerifyInclusionProof(sidecar) && VerifyKzgProofs(sidecar);

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

        SszKzgCommitment[] commitments = sidecar.KzgCommitments!;
        SszBlobCell[] cells = sidecar.Column!;
        SszKzgCommitment[] proofs = sidecar.KzgProofs!;
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
            cellIndices[i] = sidecar.Index;
        }

        try
        {
            return Ckzg.VerifyCellKzgProofBatch(flatCommitments, cellIndices, flatCells, flatProofs, count, DasKzgSetup.Handle);
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
