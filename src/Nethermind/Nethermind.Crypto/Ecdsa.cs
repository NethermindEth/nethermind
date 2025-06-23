// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    /// <summary>
    ///     for ecdsa tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class Ecdsa : IEcdsa
    {
        public Signature Sign(PrivateKey privateKey, in ValueHash256 message)
        {
            if (!SecP256k1.VerifyPrivateKey(privateKey.KeyBytes))
            {
                InvalidPrivateKey();
            }

            byte[] signatureBytes = SpanSecP256k1.SignCompact(message.Bytes, privateKey.KeyBytes, out int recoveryId);
            Signature signature = new(signatureBytes, recoveryId);

#if DEBUG
            PublicKey address = RecoverPublicKey(signature, message);
            if (!address.Equals(privateKey.PublicKey))
            {
                throw new InvalidOperationException("After signing recovery returns different address than ecdsa's");
            }
#endif
            return signature;

            [DoesNotReturn, StackTraceHidden]
            static void InvalidPrivateKey() => throw new ArgumentException("Invalid private key");
        }

        public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message)
        {
            Span<byte> publicKey = stackalloc byte[65];
            bool success = SpanSecP256k1.RecoverKeyFromCompact(publicKey, message.Bytes, signature.Bytes, signature.RecoveryId, false);
            if (!success)
            {
                return null;
            }

            return new PublicKey(publicKey);
        }

        public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message)
        {
            Span<byte> publicKey = stackalloc byte[33];
            bool success = SpanSecP256k1.RecoverKeyFromCompact(publicKey, message.Bytes, signature.Bytes, signature.RecoveryId, true);
            if (!success)
            {
                return null;
            }

            return new CompressedPublicKey(publicKey);
        }

        public static PublicKey Decompress(CompressedPublicKey compressedPublicKey)
        {
            byte[] deserialized = SecP256k1.Decompress(compressedPublicKey.Bytes);
            return new PublicKey(deserialized);
        }
    }
}
