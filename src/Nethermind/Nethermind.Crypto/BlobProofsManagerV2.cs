// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            Ckzg.Ckzg.ComputeCellsAndKzgProofs(cells, wrapper.Proofs[i], wrapper.Blobs[i], KzgPolynomialCommitments._ckzgSetup);
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

    public bool ValidateProofs(ShardBlobNetworkWrapper blobs)
    {
        return KzgPolynomialCommitments.AreCellProofsValid(blobs.Blobs, blobs.Commitments, blobs.Proofs);
    }
}
