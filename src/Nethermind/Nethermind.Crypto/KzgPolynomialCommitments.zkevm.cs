// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Crypto;

public static partial class KzgPolynomialCommitments
{
    /// <exception cref="ArgumentOutOfRangeException" />
    public static partial bool TryComputeCommitmentHashV1(ReadOnlySpan<byte> commitment, Span<byte> hashBuffer)
    {
        if (commitment.Length != 48) // KZG commitment is always 48 bytes
            return false;

        ArgumentOutOfRangeException.ThrowIfNotEqual(hashBuffer.Length, Eip4844Constants.BytesPerBlobVersionedHash, nameof(hashBuffer));

        Accelerators.Sha256(commitment, hashBuffer);

        hashBuffer[0] = KzgBlobHashVersionV1;

        return true;
    }

    /// <remarks>
    /// A commitment and a proof both at infinity, the commitment to the zero polynomial and its proof, are decided
    /// without the accelerator's pairings: the check e(C - [y]G1, G2) == e(π, [s - z]G2) then holds iff [y]G1 is
    /// the identity, that is iff y is zero, at any canonical z (EIP-4844 <c>verify_kzg_proof</c>). The opening of an
    /// empty blob takes this shape.
    /// </remarks>
    public static partial bool VerifyProof(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof) =>
        IsCompressedInfinity(commitment) && IsCompressedInfinity(proof)
            ? y.Length == 32 && !y.ContainsAnyExcept((byte)0) && z.Length == 32 && z.SequenceCompareTo(BlsModulusBigEndian) < 0
            : Accelerators.KzgPointEval(commitment, z, y, proof);

    /// <summary><see cref="BlsModulus"/> as big-endian bytes, which unlike the field need no static initializer.</summary>
    private static ReadOnlySpan<byte> BlsModulusBigEndian =>
    [
        0x73, 0xed, 0xa7, 0x53, 0x29, 0x9d, 0x7d, 0x48, 0x33, 0x39, 0xd8, 0x08, 0x09, 0xa1, 0xd8, 0x05,
        0x53, 0xbd, 0xa4, 0x02, 0xff, 0xfe, 0x5b, 0xfe, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01
    ];

    /// <summary>Whether <paramref name="point"/> is the G1 point at infinity in its only valid compressed encoding.</summary>
    private static bool IsCompressedInfinity(ReadOnlySpan<byte> point) =>
        point.Length == 48 && point[0] == 0xc0 && !point[1..].ContainsAnyExcept((byte)0);
}
