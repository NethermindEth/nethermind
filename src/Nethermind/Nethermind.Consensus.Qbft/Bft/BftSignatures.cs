// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// Conversions between <see cref="Signature"/> and the 65-byte <c>r || s || recoveryId</c> form
/// Besu writes for committed seals and message signatures.
/// </summary>
public static class BftSignatures
{
    /// <summary>Parses <c>r || s || recoveryId</c>; consensus signatures carry 0 or 1, and 2 or 3 are accepted so recovery decides.</summary>
    /// <exception cref="RlpException">The input is not 65 bytes or the recovery id is out of range.</exception>
    public static Signature Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != Signature.Size)
        {
            throw new RlpException($"BFT signature must be {Signature.Size} bytes, got {encoded.Length}.");
        }

        byte recoveryId = encoded[64];
        if (recoveryId > 3)
        {
            throw new RlpException($"BFT signature recovery id {recoveryId} is out of range.");
        }

        return new Signature(encoded[..64], recoveryId);
    }

    /// <summary>Writes <c>r || s || recoveryId</c>, the form Besu's <c>SECPSignature.encodedBytes()</c> produces.</summary>
    public static byte[] Encode(Signature signature) => signature.BytesWithRecovery;
}
