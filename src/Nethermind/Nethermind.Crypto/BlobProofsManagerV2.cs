// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;

namespace Nethermind.Crypto;

internal class BlobProofsManagerV2 : IBlobProofsManager
{
    public static BlobProofsManagerV1 Instance { get; } = new BlobProofsManagerV1();

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobs.Length][], new byte[blobs.Length * Ckzg.Ckzg.CellsPerExtBlob][], ProofVersion.V1);

        for (int i = 0; i < blobs.Length; i++)
        {
            result.Commitments[i] = new byte[Ckzg.Ckzg.BytesPerCommitment];
            result.Proofs[i] = new byte[Ckzg.Ckzg.BytesPerProof];
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        Span<byte> cells = stackalloc byte[Ckzg.Ckzg.BytesPerCell * Ckzg.Ckzg.CellsPerExtBlob];

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.Ckzg.BlobToKzgCommitment(wrapper.Commitments[i], wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);
            Ckzg.Ckzg.ComputeCellsAndKzgProofs(cells, wrapper.Proofs[i], wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);
        }
    }

    public bool ValidateLengths(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs.Length != wrapper.Commitments.Length || wrapper.Blobs.Length != wrapper.Proofs.Length * Ckzg.Ckzg.CellsPerExtBlob)
        {
            return false;
        }

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob || wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment)
            {
                return false;
            }
        }

        for (int i = 0; i < wrapper.Proofs.Length; i++)
        {
            if (wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
            {
                return false;
            }

        }

        return true;
    }

    public bool ValidateProofs(ShardBlobNetworkWrapper wrapper)
    {
        int length = wrapper.Blobs.Length * Ckzg.Ckzg.BytesPerBlob * 2;
        byte[] cellsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> cells = new(cellsArray, 0, length);

        length = wrapper.Blobs.Length * Ckzg.Ckzg.BytesPerCommitment * Ckzg.Ckzg.CellsPerExtBlob;
        byte[] flatCommitmentsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatCommitments = new(flatCommitmentsArray, 0, length);

        length = wrapper.Blobs.Length * Ckzg.Ckzg.BytesPerProof * Ckzg.Ckzg.CellsPerExtBlob;
        byte[] flatProofsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatProofs = new(flatProofsArray, 0, length);

        length = wrapper.Blobs.Length * Ckzg.Ckzg.CellsPerExtBlob;
        ulong[] indicesArray = ArrayPool<ulong>.Shared.Rent(length);
        Span<ulong> indices = new(indicesArray, 0, length);

        try
        {
            for (int i = 0; i < wrapper.Blobs.Length; i++)
            {

                Ckzg.Ckzg.ComputeCells(cells.Slice(i * Ckzg.Ckzg.BytesPerCell * Ckzg.Ckzg.CellsPerExtBlob, Ckzg.Ckzg.BytesPerCell * Ckzg.Ckzg.CellsPerExtBlob), wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);

                for (int j = 0; j < Ckzg.Ckzg.CellsPerExtBlob; j++)
                {
                    int cellNumber = i * Ckzg.Ckzg.CellsPerExtBlob + j;

                    wrapper.Commitments[i].CopyTo(flatCommitments.Slice(cellNumber * Ckzg.Ckzg.BytesPerCommitment, Ckzg.Ckzg.BytesPerCommitment));
                    indices[cellNumber] = (ulong)j;
                    wrapper.Proofs[cellNumber].CopyTo(flatProofs.Slice(cellNumber * Ckzg.Ckzg.BytesPerProof, Ckzg.Ckzg.BytesPerProof));
                }
            }

            return Ckzg.Ckzg.VerifyCellKzgProofBatch(flatCommitments, indices, cells,
                flatProofs, wrapper.Blobs.Length * Ckzg.Ckzg.CellsPerExtBlob, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cellsArray);
            ArrayPool<byte>.Shared.Return(flatCommitmentsArray);
            ArrayPool<byte>.Shared.Return(flatProofsArray);
            ArrayPool<ulong>.Shared.Return(indicesArray);
        }
    }
}
