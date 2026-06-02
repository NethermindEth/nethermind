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

    public static partial bool VerifyProof(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof) => Accelerators.KzgPointEval(commitment, z, y, proof);
}
