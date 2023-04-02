// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Secp256k1;

namespace Nethermind.Crypto
{
    /// <summary>
    ///     for ecdsa tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class Ecdsa : IEcdsa
    {
        public Signature Sign(PrivateKey privateKey, Keccak message)
        {
            return Sign(privateKey, message.ValueKeccak);
        }

        public Signature Sign(PrivateKey privateKey, ValueKeccak message)
        {
            if (!Proxy.VerifyPrivateKey(privateKey.KeyBytes))
            {
                throw new ArgumentException("Invalid private key", nameof(privateKey));
            }

            byte[] signatureBytes = Proxy.SignCompact(message.ToByteArray(), privateKey.KeyBytes, out int recoveryId);

            //// https://bitcoin.stackexchange.com/questions/59820/sign-a-tx-with-low-s-value-using-openssl

            //byte[] sBytes = signatureBytes.Slice(32, 32);
            //BigInteger s = sBytes.ToUnsignedBigInteger();
            //if (s > MaxLowS)
            //{
            //    s = LowSTransform - s;
            //    byte[] newSBytes = s.ToBigEndianByteArray();
            //    for (int i = 0; i < 32; i++)
            //    {
            //        signatureBytes[32 + 1] = newSBytes[i];
            //    }
            //}

            Signature signature = new(signatureBytes, recoveryId);

#if DEBUG
            PublicKey address = RecoverPublicKey(signature, message);
            if (!address.Equals(privateKey.PublicKey))
            {
                throw new InvalidOperationException("After signing recovery returns different address than ecdsa's");
            }
#endif

            return signature;
        }

        public PublicKey? RecoverPublicKey(Signature signature, Keccak message)
        {
            return RecoverPublicKey(signature, message.ValueKeccak);
        }

        public PublicKey? RecoverPublicKey(Signature signature, ValueKeccak message)
        {
            Span<byte> publicKey = stackalloc byte[65];
            bool success = Proxy.RecoverKeyFromCompact(publicKey, message.ToByteArray(), signature.Bytes, signature.RecoveryId, false);
            if (!success)
            {
                return null;
            }

            return new PublicKey(publicKey);
        }

        public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, Keccak message)
        {
            return RecoverCompressedPublicKey(signature, message.ValueKeccak);
        }

        public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, ValueKeccak message)
        {
            Span<byte> publicKey = stackalloc byte[33];
            bool success = Proxy.RecoverKeyFromCompact(publicKey, message.ToByteArray(), signature.Bytes, signature.RecoveryId, true);
            if (!success)
            {
                return null;
            }

            return new CompressedPublicKey(publicKey);
        }

        public PublicKey Decompress(CompressedPublicKey compressedPublicKey)
        {
            byte[] deserialized = Proxy.Decompress(compressedPublicKey.Bytes);
            return new PublicKey(deserialized);
        }
    }
}
