// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Nethermind.Crypto;

public interface IBlobProofsManager : IBlobProofsBuilder, IBlobProofsVerifier, IKzg
{
    static IBlobProofsManager For
        (ProofVersion version) => version switch
        {
            ProofVersion.V0 => BlobProofsManagerV0.Instance,
            ProofVersion.V1 => BlobProofsManagerV1.Instance,
            _ => throw new NotSupportedException(),
        };
}

public interface IKzg
{
    public Task InitAsync(ILogger logger = default);

    public static readonly UInt256 BlsModulus =
        UInt256.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513",
            System.Globalization.NumberStyles.Integer);

    public const byte KzgBlobHashVersionV1 = 1;

    public const byte BytesPerBlobVersionedHash = 32;

    public bool TryComputeCommitmentHashV1(ReadOnlySpan<byte> commitment, Span<byte> hashBuffer)
    {
        if (commitment.Length != Ckzg.BytesPerCommitment)
        {
            return false;
        }

        if (hashBuffer.Length != BytesPerBlobVersionedHash)
        {
            throw new ArgumentException($"{nameof(hashBuffer)} should be {BytesPerBlobVersionedHash} bytes", nameof(hashBuffer));
        }

        if (SHA256.TryHashData(commitment, hashBuffer, out _))
        {
            hashBuffer[0] = KzgBlobHashVersionV1;
            return true;
        }

        return false;
    }

    public bool VerifyProof(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof);
}

public interface IBlobProofsBuilder : IKzg
{
    ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs);

    byte[][] ComputeHashes(ShardBlobNetworkWrapper wrapper)
    {
        byte[][] hashes = new byte[wrapper.Blobs.Length][];
        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            hashes[i] = new byte[IKzg.BytesPerBlobVersionedHash];
            TryComputeCommitmentHashV1(wrapper.Commitments[i], hashes[i]);
        }
        return hashes;
    }

    void ComputeProofsAndCommitments(ShardBlobNetworkWrapper preallocatedWrappers);

}

public interface IBlobProofsVerifier : IKzg
{

    bool ValidateLengths(ShardBlobNetworkWrapper blobs);
    public bool ValidateHashes(ShardBlobNetworkWrapper blobs, byte[][] blobVersionedHashes)
    {
        Span<byte> hash = stackalloc byte[IKzg.BytesPerBlobVersionedHash];

        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            if (blobVersionedHashes[i].Length != IKzg.BytesPerBlobVersionedHash || blobVersionedHashes[i][0] != IKzg.KzgBlobHashVersionV1)
            {
                return false;
            }
        }

        for (int i = 0; i < blobs.Blobs.Length; i++)
        {
            if (!TryComputeCommitmentHashV1(blobs.Commitments[i], hash) || !hash.SequenceEqual(blobVersionedHashes[i].AsSpan()))
            {
                return false;
            }
        }

        return true;
    }

    bool ValidateProofs(ShardBlobNetworkWrapper blobs);
}
