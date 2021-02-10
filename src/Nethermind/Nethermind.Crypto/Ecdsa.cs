//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            if (!Proxy.VerifyPrivateKey(privateKey.KeyBytes))
            {
                throw new ArgumentException("Invalid private key", nameof(privateKey));
            }

            byte[] signatureBytes = Proxy.SignCompact(message.Bytes, privateKey.KeyBytes, out int recoveryId);

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
            Span<byte> publicKey = stackalloc byte[65];
            bool success = Proxy.RecoverKeyFromCompact(publicKey, message.Bytes, signature.Bytes, signature.RecoveryId, false);
            if (!success)
            {
                return null;
            }
            
            return new PublicKey(publicKey);
        }
    }
}
