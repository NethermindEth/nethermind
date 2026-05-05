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

        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobCount][], new byte[proofCount][], ProofVersion.V1);

        for (int i = 0; i < blobCount; i++)
        {
            result.Commitments[i] = new byte[Ckzg.BytesPerCommitment];
            for (int j = 0; j < Ckzg.CellsPerExtBlob; j++)
            {
                result.Proofs[i * Ckzg.CellsPerExtBlob + j] = new byte[Ckzg.BytesPerProof];
            }
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        using ArrayPoolSpan<byte> cells = new(Ckzg.BytesPerCell * Ckzg.CellsPerExtBlob);
        using ArrayPoolSpan<byte> proofs = new(Ckzg.BytesPerProof * Ckzg.CellsPerExtBlob);

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.BlobToKzgCommitment(wrapper.Commitments[i], wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);
            Ckzg.ComputeCellsAndKzgProofs(cells, proofs, wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);

            for (int j = 0; j < Ckzg.CellsPerExtBlob; j++)
            {
                proofs.Slice(Ckzg.BytesPerProof * j, Ckzg.BytesPerProof).CopyTo(wrapper.Proofs[i * Ckzg.CellsPerExtBlob + j]);
            }
        }
    }

    public bool ValidateLengths(ShardBlobNetworkWrapper wrapper)
    {
        int blobCount = wrapper.Blobs.Length;
        int proofCount = blobCount * Ckzg.CellsPerExtBlob;

        if (blobCount != wrapper.Commitments.Length || proofCount != wrapper.Proofs.Length)
        {
            return false;
        }

        for (int i = 0; i < blobCount; i++)
        {
            if (wrapper.Blobs[i].Length != Ckzg.BytesPerBlob || wrapper.Commitments[i].Length != Ckzg.BytesPerCommitment)
            {
                return false;
            }
        }

        for (int i = 0; i < proofCount; i++)
        {
            if (wrapper.Proofs[i].Length != Ckzg.BytesPerProof)
            {
                return false;
            }

        }

        return true;
    }

    public bool ValidateProofs(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Version is not ProofVersion.V1)
        {
            return false;
        }

        int blobCount = wrapper.Blobs.Length;
        int cellCount = blobCount * Ckzg.CellsPerExtBlob;

        using ArrayPoolSpan<byte> cells = new(blobCount * Ckzg.BytesPerBlob * 2);
        using ArrayPoolSpan<byte> flatCommitments = new(cellCount * Ckzg.BytesPerCommitment);
        using ArrayPoolSpan<byte> flatProofs = new(cellCount * Ckzg.BytesPerProof);
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
                    wrapper.Proofs[cellNumber].CopyTo(flatProofs.Slice(cellNumber * Ckzg.BytesPerProof, Ckzg.BytesPerProof));
                }
            }

            return Ckzg.VerifyCellKzgProofBatch(flatCommitments, indices, cells,
                flatProofs, cellCount, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
