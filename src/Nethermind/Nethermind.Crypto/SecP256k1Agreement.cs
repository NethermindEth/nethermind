// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace Nethermind.Crypto;

/// <summary>
/// secp256k1 key-agreement helpers for protocols that need the serialized shared EC point.
/// </summary>
public static class SecP256k1Agreement
{
    /// <summary>
    /// Computes the compressed shared EC point for ECDH.
    /// </summary>
    public static byte[] AgreeCompressed(PublicKey publicKey, PrivateKey privateKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(privateKey);

        ECPoint point = BouncyCrypto.DomainParameters.Curve.DecodePoint(publicKey.PrefixedBytes);
        return AgreeCompressed(point, privateKey);
    }

    /// <summary>
    /// Computes the compressed shared EC point for ECDH.
    /// </summary>
    public static byte[] AgreeCompressed(CompressedPublicKey publicKey, PrivateKey privateKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(privateKey);

        ECPoint point = BouncyCrypto.DomainParameters.Curve.DecodePoint(publicKey.Bytes);
        return AgreeCompressed(point, privateKey);
    }

    private static byte[] AgreeCompressed(ECPoint point, PrivateKey privateKey)
    {
        BigInteger privateScalar = new(1, privateKey.KeyBytes);
        return point.Multiply(privateScalar).Normalize().GetEncoded(compressed: true);
    }
}
