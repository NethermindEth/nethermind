// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    /// <summary>
    ///     for ecdsa tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class EthereumEcdsa(ulong chainId) : Ecdsa, IEthereumEcdsa
    {
        public static readonly BigInteger MaxLowS =
            BigInteger.Parse("7FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0",
                NumberStyles.HexNumber);

        public static readonly BigInteger LowSTransform =
            BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
                NumberStyles.HexNumber);

        public ulong ChainId => chainId;

        public Address? RecoverAddress(Signature signature, in ValueHash256 message) => RecoverAddress(signature.Bytes, signature.RecoveryId, message.Bytes);

        private static Address? RecoverAddress(Span<byte> signatureBytes64, byte v, ReadOnlySpan<byte> message)
        {
            Span<byte> publicKey = stackalloc byte[65];
            bool success = SpanSecP256k1.RecoverKeyFromCompact(
                publicKey,
                message,
                signatureBytes64,
                v,
                false);

            return !success ? null : PublicKey.ComputeAddress(publicKey.Slice(1, 64));
        }

        public static bool RecoverAddressRaw(ReadOnlySpan<byte> signatureBytes64, byte v, ReadOnlySpan<byte> message, Span<byte> resultPublicKey65) =>
            SpanSecP256k1.RecoverKeyFromCompact(
                resultPublicKey65,
                message,
                signatureBytes64,
                v,
                false);
    }
}
