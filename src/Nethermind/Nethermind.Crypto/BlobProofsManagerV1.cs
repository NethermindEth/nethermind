// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Crypto;

internal class BlobProofsManagerV1 : IBlobProofsManager
{
    public static BlobProofsManagerV1 Instance { get; } = new BlobProofsManagerV1();

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        int blobCount = blobs.Length;
        int proofCount = blobCount * Ckzg.CellsPerExtBlob;

        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobCount][], new byte[proofCount * Ckzg.BytesPerProof], ProofVersion.V1);

        for (int i = 0; i < blobCount; i++)
        {
            result.Commitments[i] = new byte[Ckzg.BytesPerCommitment];
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        using ArrayPoolSpan<byte> cells = new(Ckzg.BytesPerCell * Ckzg.CellsPerExtBlob);
        const int proofsPerBlobLength = Ckzg.BytesPerProof * Ckzg.CellsPerExtBlob;

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.BlobToKzgCommitment(wrapper.Commitments[i], wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);
            Ckzg.ComputeCellsAndKzgProofs(cells, wrapper.Proofs.AsSpan(i * proofsPerBlobLength, proofsPerBlobLength), wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);
        }
    }

    public bool ValidateLengths(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs is null || wrapper.Commitments is null || wrapper.Proofs is null)
        {
            return false;
        }

        int blobCount = wrapper.Commitments.Length;
        int proofCount = blobCount * Ckzg.CellsPerExtBlob;

        if ((wrapper.Blobs.Length != 0 && blobCount != wrapper.Blobs.Length) || proofCount * Ckzg.BytesPerProof != wrapper.Proofs.Length)
        {
            return false;
        }

        for (int i = 0; i < blobCount; i++)
        {
            if (wrapper.Commitments[i] is not { Length: Ckzg.BytesPerCommitment })
            {
                return false;
            }

            if (wrapper.Blobs.Length != 0)
            {
                if (wrapper.Blobs[i] is null)
                {
                    return false;
                }

                int blobLength = wrapper.Blobs[i].Length;
                if (blobLength != 0 && blobLength != Ckzg.BytesPerBlob)
                {
                    return false;
                }
            }
        }

        if (wrapper.Cells is null)
        {
            return wrapper.CellMask.IsEmpty;
        }

        if (wrapper.CellMask.IsEmpty || wrapper.Cells.Length != blobCount * wrapper.CellMask.Count)
        {
            return false;
        }

        for (int i = 0; i < wrapper.Cells.Length; i++)
        {
            if (wrapper.Cells[i] is not { Length: Ckzg.BytesPerCell })
            {
                return false;
            }
        }

        return true;
    }

    public bool ValidateProofs(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Version is not ProofVersion.V1 || !ValidateLengths(wrapper))
        {
            return false;
        }

        if (wrapper.Commitments.Length == 0)
        {
            return wrapper.Blobs.Length == 0
                && wrapper.Proofs.Length == 0
                && wrapper.Cells is null
                && wrapper.CellMask.IsEmpty;
        }

        if (!wrapper.HasFullBlobs())
        {
            return wrapper.Cells is not null
                && !wrapper.CellMask.IsEmpty
                && BlobCellsHelper.ValidateCells(wrapper);
        }

        int blobCount = wrapper.Blobs.Length;
        int cellCount = blobCount * Ckzg.CellsPerExtBlob;

        using ArrayPoolSpan<byte> cells = new(blobCount * Ckzg.BytesPerBlob * 2);
        using ArrayPoolSpan<byte> flatCommitments = new(cellCount * Ckzg.BytesPerCommitment);
        using ArrayPoolSpan<ulong> indices = new(cellCount);

        try
        {
            for (int i = 0; i < blobCount; i++)
            {

                Ckzg.ComputeCells(cells.Slice(i * Ckzg.BytesPerCell * Ckzg.CellsPerExtBlob, Ckzg.BytesPerCell * Ckzg.CellsPerExtBlob), wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);

                for (int j = 0; j < Ckzg.CellsPerExtBlob; j++)
                {
                    int cellNumber = i * Ckzg.CellsPerExtBlob + j;

                    wrapper.Commitments[i].CopyTo(flatCommitments.Slice(cellNumber * Ckzg.BytesPerCommitment, Ckzg.BytesPerCommitment));
                    indices[cellNumber] = (ulong)j;
                }
            }

            return Ckzg.VerifyCellKzgProofBatch(flatCommitments, indices, cells,
                wrapper.Proofs, cellCount, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
