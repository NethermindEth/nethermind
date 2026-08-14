// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using CkzgLib;
using Nethermind.Core;

namespace Nethermind.Crypto;

public static partial class KzgPolynomialCommitments
{
    /// <exception cref="ArgumentOutOfRangeException" />
    public static partial bool TryComputeCommitmentHashV1(ReadOnlySpan<byte> commitment, Span<byte> hashBuffer)
    {
        if (commitment.Length != Ckzg.BytesPerCommitment)
            return false;

        ArgumentOutOfRangeException.ThrowIfNotEqual(hashBuffer.Length, Eip4844Constants.BytesPerBlobVersionedHash, nameof(hashBuffer));

        if (SHA256.TryHashData(commitment, hashBuffer, out _))
        {
            hashBuffer[0] = KzgBlobHashVersionV1;
            return true;
        }

        return false;
    }

    public static partial bool VerifyProof(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof)
    {
        try
        {
            return Ckzg.VerifyKzgProof(commitment, z, y, proof, _ckzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}

