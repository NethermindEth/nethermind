// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Crypto;

public interface IBlobProofsManager : IBlobProofsBuilder, IBlobProofsVerifier
{
    static IBlobProofsManager For
        (ProofVersion version) => version switch
        {
            ProofVersion.V0 => BlobProofsManagerV0.Instance,
            ProofVersion.V1 => BlobProofsManagerV1.Instance,
            _ => throw new NotSupportedException(),
        };
}

public interface IBlobProofsBuilder
{
    ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs);

    byte[][] ComputeHashes(ShardBlobNetworkWrapper wrapper)
    {
        byte[][] hashes = new byte[wrapper.Blobs.Length][];
        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            hashes[i] = new byte[KzgPolynomialCommitments.BytesPerBlobVersionedHash];
            KzgPolynomialCommitments.TryComputeCommitmentHashV1(wrapper.Commitments[i], hashes[i]);
        }
        return hashes;
    }

    void ComputeProofsAndCommitments(ShardBlobNetworkWrapper preallocatedWrappers);

}

public interface IBlobProofsVerifier
{

    bool ValidateLengths(ShardBlobNetworkWrapper blobs);
    public bool ValidateHashes(ShardBlobNetworkWrapper blobs, byte[][] blobVersionedHashes)
    {
        Span<byte> hash = stackalloc byte[KzgPolynomialCommitments.BytesPerBlobVersionedHash];

        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            if (blobVersionedHashes[i].Length != KzgPolynomialCommitments.BytesPerBlobVersionedHash || blobVersionedHashes[i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
            {
                return false;
            }
        }

        for (int i = 0; i < blobs.Blobs.Length; i++)
        {
            if (!KzgPolynomialCommitments.TryComputeCommitmentHashV1(blobs.Commitments[i], hash) || !hash.SequenceEqual(blobVersionedHashes[i].AsSpan()))
            {
                return false;
            }
        }

        return true;
    }

    bool ValidateProofs(ShardBlobNetworkWrapper blobs);
}
