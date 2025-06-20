// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using System;

namespace Nethermind.Crypto;

internal class EthKzgBlobProofsManagerV1 : IBlobProofsManager
{
    public static EthKzgBlobProofsManagerV1 Instance { get; } = new();

    readonly EthKZG.EthKZG kzg = new(2);

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobs.Length][], new byte[blobs.Length * Ckzg.CellsPerExtBlob][], ProofVersion.V1);


        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            wrapper.Commitments[i] = kzg.BlobToKzgCommitment(wrapper.Blobs[i]);
            (byte[][] _, byte[][] proofs) = kzg.ComputeCellsAndKZGProofs(wrapper.Blobs[i]);

            proofs.CopyTo(wrapper.Proofs[i], i * Ckzg.CellsPerExtBlob);
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
        byte[][] cells = new byte[wrapper.Blobs.Length * Ckzg.CellsPerExtBlob][];
        byte[][] proofs = new byte[wrapper.Blobs.Length * Ckzg.CellsPerExtBlob][];
        ulong[] indices = new ulong[wrapper.Blobs.Length * Ckzg.CellsPerExtBlob];

        try
        {
            for (int i = 0; i < wrapper.Blobs.Length; i++)
            {
                kzg.ComputeCells(wrapper.Blobs[i]).CopyTo(cells, i * Ckzg.CellsPerExtBlob);
                wrapper.Proofs[i].CopyTo(proofs, i * Ckzg.CellsPerExtBlob);

                for (int j = 0; j < Ckzg.CellsPerExtBlob; j++)
                {
                    int cellNumber = i * Ckzg.CellsPerExtBlob + j;
                    indices[cellNumber] = (ulong)j;
                }
            }

            return kzg.VerifyCellKZGProofBatch(wrapper.Commitments, indices, cells, proofs);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
