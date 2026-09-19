// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Spec;

/// <summary>Computes the 4-byte p2p fork digest for an epoch.</summary>
/// <remarks>
/// Implements <c>compute_fork_digest</c> including the EIP-7892 (Fulu) extension: from the Fulu
/// fork onward, the base digest is XOR-masked with the first 4 bytes of
/// <c>sha256(uint64_le(blob_params.epoch) ++ uint64_le(blob_params.max_blobs_per_block))</c>,
/// so blob-parameter-only forks rotate the digest without a fork version bump.
/// </remarks>
public static class ForkDigest
{
    public static byte[] Compute(BeaconChainSpec spec, ulong epoch)
    {
        Hash256 forkDataRoot = Domains.ComputeForkDataRoot(spec.VersionForEpoch(epoch), spec.GenesisValidatorsRoot);
        byte[] digest = forkDataRoot.Bytes[..4].ToArray();

        if (spec.GetBlobParameters(epoch) is not { } blobParameters)
        {
            return digest;
        }

        Span<byte> input = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(input, blobParameters.Epoch);
        BinaryPrimitives.WriteUInt64LittleEndian(input[8..], blobParameters.MaxBlobsPerBlock);
        Span<byte> mask = stackalloc byte[32];
        SHA256.HashData(input, mask);

        for (int i = 0; i < digest.Length; i++)
        {
            digest[i] ^= mask[i];
        }

        return digest;
    }
}
