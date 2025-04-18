// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;

namespace Nethermind.Crypto;

internal class BlobProofsManagerV1 : IBlobProofsManager
{
    public static BlobProofsManagerV1 Instance { get; } = new BlobProofsManagerV1();

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        ShardBlobNetworkWrapper result = new(new byte[blobs.Length * Ckzg.Ckzg.BytesPerBlob], new byte[blobs.Length * Ckzg.Ckzg.BytesPerCommitment], new byte[blobs.Length * Ckzg.Ckzg.BytesPerProof * Ckzg.Ckzg.CellsPerExtBlob], ProofVersion.V0);

        for (var i = 0; i < blobs.Length; i++)
        {
            blobs[i].CopyTo(result.BlobAt(i));
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        Span<byte> cells = stackalloc byte[Ckzg.Ckzg.BytesPerCell * Ckzg.Ckzg.CellsPerExtBlob];

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.Ckzg.BlobToKzgCommitment(wrapper.CommitmentAt(i).Span, wrapper.BlobAt(i).Span, KzgPolynomialCommitments.CkzgSetup);
            Ckzg.Ckzg.ComputeCellsAndKzgProofs(cells, wrapper.ProofsAt(i).Span, wrapper.BlobAt(i).Span, KzgPolynomialCommitments.CkzgSetup);
        }
    }

    public bool ValidateLengths(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs.Length != wrapper.Count * Ckzg.Ckzg.BytesPerBlob || wrapper.Proofs.Length != wrapper.Count * Ckzg.Ckzg.BytesPerProof * Ckzg.Ckzg.CellsPerExtBlob || wrapper.Commitments.Length != wrapper.Count * Ckzg.Ckzg.BytesPerCommitment)
        {
            return false;
        }

        return true;
    }

    public bool ValidateProofs(ShardBlobNetworkWrapper wrapper)
    {
        int length = wrapper.Blobs.Length * Ckzg.Ckzg.BytesPerBlob * 2;
        byte[] cellsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> cells = new(cellsArray, 0, length);

        length = wrapper.Blobs.Length * Ckzg.Ckzg.CellsPerExtBlob;
        ulong[] indicesArray = ArrayPool<ulong>.Shared.Rent(length);
        Span<ulong> indices = new(indicesArray, 0, length);

        try
        {
            for (int i = 0; i < wrapper.Blobs.Length; i++)
            {
                Ckzg.Ckzg.ComputeCells(cells.Slice(i * Ckzg.Ckzg.BytesPerCell * Ckzg.Ckzg.CellsPerExtBlob, Ckzg.Ckzg.BytesPerCell * Ckzg.Ckzg.CellsPerExtBlob), wrapper.BlobAt(i).Span, KzgPolynomialCommitments.CkzgSetup);

                for (int j = 0; j < Ckzg.Ckzg.CellsPerExtBlob; j++)
                {
                    int cellNumber = i * Ckzg.Ckzg.CellsPerExtBlob + j;
                    indices[cellNumber] = (ulong)j;
                }
            }

            return Ckzg.Ckzg.VerifyCellKzgProofBatch(wrapper.Commitments, indices, cells,
                wrapper.Proofs, wrapper.Blobs.Length * Ckzg.Ckzg.CellsPerExtBlob, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cellsArray);
            ArrayPool<ulong>.Shared.Return(indicesArray);
        }
    }
}
