// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Crypto;

internal class BlobProofsManagerV1 : IBlobProofsManager
{
    public static BlobProofsManagerV1 Instance { get; } = new BlobProofsManagerV1();

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobs.Length][], new byte[blobs.Length * Ckzg.CellsPerExtBlob][], ProofVersion.V1);

        for (int i = 0; i < blobs.Length; i++)
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
        if (wrapper.Blobs.Length != wrapper.Commitments.Length || wrapper.Blobs.Length * Ckzg.CellsPerExtBlob != wrapper.Proofs.Length)
        {
            return false;
        }

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            if (wrapper.Blobs[i].Length != Ckzg.BytesPerBlob || wrapper.Commitments[i].Length != Ckzg.BytesPerCommitment)
            {
                return false;
            }
        }

        for (int i = 0; i < wrapper.Proofs.Length; i++)
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
        using ArrayPoolSpan<byte> cells = new(wrapper.Blobs.Length * Ckzg.BytesPerBlob * 2);
        using ArrayPoolSpan<byte> flatCommitments = new(wrapper.Blobs.Length * Ckzg.BytesPerCommitment * Ckzg.CellsPerExtBlob);
        using ArrayPoolSpan<byte> flatProofs = new(wrapper.Blobs.Length * Ckzg.BytesPerProof * Ckzg.CellsPerExtBlob);
        using ArrayPoolSpan<ulong> indices = new(wrapper.Blobs.Length * Ckzg.CellsPerExtBlob);

        try
        {
            for (int i = 0; i < wrapper.Blobs.Length; i++)
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
                flatProofs, wrapper.Blobs.Length * Ckzg.CellsPerExtBlob, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
