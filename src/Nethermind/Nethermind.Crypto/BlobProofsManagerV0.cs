// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;

namespace Nethermind.Crypto;

internal class BlobProofsManagerV0 : IBlobProofsManager
{
    public static BlobProofsManagerV0 Instance { get; } = new BlobProofsManagerV0();

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        ShardBlobNetworkWrapper result = new(new byte[blobs.Length * Ckzg.Ckzg.BytesPerBlob], new byte[blobs.Length * Ckzg.Ckzg.BytesPerCommitment], new byte[blobs.Length * Ckzg.Ckzg.BytesPerProof], ProofVersion.V0);

        for (var i = 0; i < blobs.Length; i++)
        {
            blobs[i].CopyTo(result.BlobAt(i));
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.Ckzg.BlobToKzgCommitment(wrapper.CommitmentAt(i).Span, wrapper.BlobAt(i).Span, KzgPolynomialCommitments.CkzgSetup);
            Ckzg.Ckzg.ComputeBlobKzgProof(wrapper.ProofsAt(i).Span, wrapper.BlobAt(i).Span, wrapper.CommitmentAt(i).Span, KzgPolynomialCommitments.CkzgSetup);
        }
    }

    public bool ValidateLengths(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs.Length != wrapper.Count * Ckzg.Ckzg.BytesPerBlob || wrapper.Proofs.Length != wrapper.Count * Ckzg.Ckzg.BytesPerProof || wrapper.Commitments.Length != wrapper.Count * Ckzg.Ckzg.BytesPerCommitment)
        {
            return false;
        }

        return true;
    }

    public bool ValidateProofs(ShardBlobNetworkWrapper wrapper)
    {
        try
        {
            return Ckzg.Ckzg.VerifyBlobKzgProofBatch(wrapper.Blobs, wrapper.Commitments, wrapper.Proofs, wrapper.Count, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
