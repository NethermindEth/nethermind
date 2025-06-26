// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Crypto;

internal class BlobProofsManagerV0 : IBlobProofsManager
{
    public static BlobProofsManagerV0 Instance { get; } = new BlobProofsManagerV0();

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobs.Length][], new byte[blobs.Length][], ProofVersion.V0);

        for (int i = 0; i < blobs.Length; i++)
        {
            result.Commitments[i] = new byte[Ckzg.BytesPerCommitment];
            result.Proofs[i] = new byte[Ckzg.BytesPerProof];
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.BlobToKzgCommitment(wrapper.Commitments[i], wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);
            Ckzg.ComputeBlobKzgProof(wrapper.Proofs[i], wrapper.Blobs[i], wrapper.Commitments[i], KzgPolynomialCommitments.CkzgSetup);
        }
    }

    public bool ValidateLengths(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs.Length != wrapper.Commitments.Length || wrapper.Blobs.Length != wrapper.Proofs.Length)
        {
            return false;
        }

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            if (wrapper.Blobs[i].Length != Ckzg.BytesPerBlob || wrapper.Commitments[i].Length != Ckzg.BytesPerCommitment || wrapper.Proofs[i].Length != Ckzg.BytesPerProof)
            {
                return false;
            }
        }

        return true;
    }

    public bool ValidateProofs(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs.Length is 1 && wrapper.Commitments.Length is 1 && wrapper.Proofs.Length is 1)
        {
            try
            {
                return Ckzg.VerifyBlobKzgProof(wrapper.Blobs[0], wrapper.Commitments[0], wrapper.Proofs[0], KzgPolynomialCommitments.CkzgSetup);
            }
            catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
            {
                return false;
            }
        }

        int length = wrapper.Blobs.Length * Ckzg.BytesPerBlob;
        using ArrayPoolSpan<byte> flatBlobs = new(length);

        length = wrapper.Blobs.Length * Ckzg.BytesPerCommitment;
        using ArrayPoolSpan<byte> flatCommitments = new(length);

        length = wrapper.Blobs.Length * Ckzg.BytesPerProof;
        using ArrayPoolSpan<byte> flatProofs = new(length);

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            wrapper.Blobs[i].CopyTo(flatBlobs.Slice(i * Ckzg.BytesPerBlob, Ckzg.BytesPerBlob));
            wrapper.Commitments[i].CopyTo(flatCommitments.Slice(i * Ckzg.BytesPerCommitment, Ckzg.BytesPerCommitment));
            wrapper.Proofs[i].CopyTo(flatProofs.Slice(i * Ckzg.BytesPerProof, Ckzg.BytesPerProof));
        }

        try
        {
            return Ckzg.VerifyBlobKzgProofBatch(flatBlobs, flatCommitments, flatProofs, wrapper.Blobs.Length, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
