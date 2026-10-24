// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using CkzgLib;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.BeaconChain.DataAvailability;

internal static class LocalBlobColumns
{
    internal static DataColumnSidecar[] Create(SignedBeaconBlock signed, IReadOnlyList<BlobAndProofV2?> blobs, IReadOnlyList<ulong> sampledColumns)
    {
        BeaconBlock block = signed.Message!;
        BeaconBlockBody body = block.Body!;
        SszKzgCommitment[] commitments = body.BlobKzgCommitments!;
        if (blobs.Count != commitments.Length || blobs.Count == 0 || sampledColumns.Count == 0) return [];
        foreach (BlobAndProofV2? blob in blobs)
        {
            if (blob is not { Blob.Length: Ckzg.BytesPerBlob, Proofs.Length: Ckzg.CellsPerExtBlob }) return [];
            foreach (byte[] proof in blob.Proofs) if (proof is not { Length: Ckzg.BytesPerProof }) return [];
        }

        DasKzg.EnsureInitialized();
        Hash256[] branch = CommitmentsProof(body);
        SignedBeaconBlockHeader header = new()
        {
            Message = new BeaconBlockHeader { Slot = block.Slot, ProposerIndex = block.ProposerIndex, ParentRoot = block.ParentRoot, StateRoot = block.StateRoot, BodyRoot = SszRoots.HashTreeRoot(body) },
            Signature = signed.Signature,
        };
        DataColumnSidecar[] columns = new DataColumnSidecar[sampledColumns.Count];
        for (int c = 0; c < columns.Length; c++)
        {
            columns[c] = new DataColumnSidecar
            {
                Index = sampledColumns[c],
                Column = new SszBlobCell[blobs.Count],
                KzgProofs = new SszKzgCommitment[blobs.Count],
                KzgCommitments = commitments,
                SignedBlockHeader = header,
                KzgCommitmentsInclusionProof = branch,
            };
        }

        using ArrayPoolSpan<byte> cells = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> sampledCells = new(columns.Length * Ckzg.BytesPerCell);
        using ArrayPoolSpan<byte> proofs = new(columns.Length * Ckzg.BytesPerProof);
        using ArrayPoolSpan<byte> rowCommitments = new(columns.Length * Ckzg.BytesPerCommitment);
        Span<ulong> indices = stackalloc ulong[columns.Length];
        for (int c = 0; c < indices.Length; c++) indices[c] = sampledColumns[c];
        for (int b = 0; b < blobs.Count; b++)
        {
            KzgPolynomialCommitments.ComputeCells(blobs[b]!.Blob, cells);
            for (int c = 0; c < columns.Length; c++)
            {
                int index = (int)indices[c];
                Span<byte> cell = sampledCells.Slice(c * Ckzg.BytesPerCell, Ckzg.BytesPerCell);
                cells.Slice(index * Ckzg.BytesPerCell, Ckzg.BytesPerCell).CopyTo(cell);
                columns[c].Column![b] = SszBlobCell.FromSpan(cell);
                columns[c].KzgProofs![b] = SszKzgCommitment.FromSpan(blobs[b]!.Proofs[index]);
                blobs[b]!.Proofs[index].CopyTo(proofs.Slice(c * Ckzg.BytesPerProof, Ckzg.BytesPerProof));
                commitments[b].AsSpan().CopyTo(rowCommitments.Slice(c * Ckzg.BytesPerCommitment, Ckzg.BytesPerCommitment));
            }
            // Verify local responses against the block before any column enters the verified pool.
            if (!KzgPolynomialCommitments.VerifyCellProofBatch(rowCommitments, indices, sampledCells, proofs, indices.Length)) return [];
        }
        return columns;
    }

    private static Hash256[] CommitmentsProof(BeaconBlockBody body)
    {
        Span<UInt256> roots = stackalloc UInt256[16];
        roots.Clear();
        Merkle.Merkleize(out roots[0], body.RandaoReveal.Bytes);
        Eth1Data.Merkleize(body.Eth1Data, out roots[1]);
        roots[2] = new UInt256(body.Graffiti!.Bytes);
        ProposerSlashing.MerkleizeList(body.ProposerSlashings, 16, out roots[3]);
        AttesterSlashing.MerkleizeList(body.AttesterSlashings, 1, out roots[4]);
        Attestation.MerkleizeList(body.Attestations, 8, out roots[5]);
        Deposit.MerkleizeList(body.Deposits, 16, out roots[6]);
        SignedVoluntaryExit.MerkleizeList(body.VoluntaryExits, 16, out roots[7]);
        SyncAggregate.Merkleize(body.SyncAggregate, out roots[8]);
        Types.ExecutionPayload.Merkleize(body.ExecutionPayload, out roots[9]);
        SignedBlsToExecutionChange.MerkleizeList(body.BlsToExecutionChanges, 16, out roots[10]);
        roots[11] = new UInt256(DataColumnSidecarVerifier.ComputeCommitmentsListRoot(body.BlobKzgCommitments!).Bytes);
        ExecutionRequests.Merkleize(body.ExecutionRequests, out roots[12]);
        Hash256[] proof = new Hash256[Eip7594DasConstants.KzgCommitmentsInclusionProofDepth];
        int index = Eip7594DasConstants.BlobKzgCommitmentsSubtreeIndex;
        Span<byte> pair = stackalloc byte[64];
        for (int depth = 0, width = 16; depth < proof.Length; depth++, width >>= 1, index >>= 1)
        {
            proof[depth] = new Hash256(roots[index ^ 1].ToLittleEndian());
            for (int i = 0; i < width / 2; i++)
            {
                roots[2 * i].ToLittleEndian(pair[..32]);
                roots[2 * i + 1].ToLittleEndian(pair[32..]);
                roots[i] = new UInt256(SHA256.HashData(pair));
            }
        }
        return proof;
    }
}
