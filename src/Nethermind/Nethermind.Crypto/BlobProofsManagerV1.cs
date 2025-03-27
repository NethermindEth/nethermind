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
        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobs.Length][], new byte[blobs.Length][], ProofVersion.V1);

        for (int i = 0; i < blobs.Length; i++)
        {
            result.Commitments[i] = new byte[Ckzg.Ckzg.BytesPerCommitment];
            result.Proofs[i] = new byte[Ckzg.Ckzg.BytesPerProof];
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.Ckzg.BlobToKzgCommitment(wrapper.Commitments[i], wrapper.Blobs[i], KzgPolynomialCommitments.CkzgSetup);
            Ckzg.Ckzg.ComputeBlobKzgProof(wrapper.Proofs[i], wrapper.Blobs[i], wrapper.Commitments[i], KzgPolynomialCommitments.CkzgSetup);
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
            if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob || wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment || wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
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
                return Ckzg.Ckzg.VerifyBlobKzgProof(wrapper.Blobs[0], wrapper.Commitments[0], wrapper.Proofs[0], KzgPolynomialCommitments.CkzgSetup);
            }
            catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
            {
                return false;
            }
        }

        int length = wrapper.Blobs.Length * Ckzg.Ckzg.BytesPerBlob;
        byte[] flatBlobsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatBlobs = new(flatBlobsArray, 0, length);

        length = wrapper.Blobs.Length * Ckzg.Ckzg.BytesPerCommitment;
        byte[] flatCommitmentsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatCommitments = new(flatCommitmentsArray, 0, length);

        length = wrapper.Blobs.Length * Ckzg.Ckzg.BytesPerProof;
        byte[] flatProofsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatProofs = new(flatProofsArray, 0, length);

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            wrapper.Blobs[i].CopyTo(flatBlobs.Slice(i * Ckzg.Ckzg.BytesPerBlob, Ckzg.Ckzg.BytesPerBlob));
            wrapper.Commitments[i].CopyTo(flatCommitments.Slice(i * Ckzg.Ckzg.BytesPerCommitment, Ckzg.Ckzg.BytesPerCommitment));
            wrapper.Proofs[i].CopyTo(flatProofs.Slice(i * Ckzg.Ckzg.BytesPerProof, Ckzg.Ckzg.BytesPerProof));
        }

        try
        {
            return Ckzg.Ckzg.VerifyBlobKzgProofBatch(flatBlobs, flatCommitments, flatProofs, wrapper.Blobs.Length, KzgPolynomialCommitments.CkzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(flatBlobsArray);
            ArrayPool<byte>.Shared.Return(flatCommitmentsArray);
            ArrayPool<byte>.Shared.Return(flatProofsArray);
        }
    }
}
