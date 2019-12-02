//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Nethermind.Bls
{
    public static class BlsProxy
    {
        private const int MclBnFpUnitSize = 6;

        private const int MclBnFrUnitSize = 4;

        private const int MclBls12_381CurveId = 5;

        private const int BlsCompilerTimeVarAdjustment = 200; 
        
        private const int MclBnCompileTimeVar = MclBnFrUnitSize * 10 + MclBnFpUnitSize + BlsCompilerTimeVarAdjustment;

        private const int BlsPublicKeyLength = 3 * 48;
        private const int PublicKeyLength = 48;
        
        private const int BlsPrivateKeyLength = 32;
        private const int PrivateKeyLength = 32;
        
        private const int BlsSignatureLength = 3 * 96;
        private const int SignatureLength = 96;
        
        private const int HashLength = 32;
        private const int DomainLength = 8;

        static BlsProxy()
        {
            int initResult = blsInit(MclBls12_381CurveId, MclBnCompileTimeVar);
            if (initResult != 0)
            {
                throw new CryptographicException($"Unable to load the BLS lib: {initResult}");
            }

            blsSetETHserialization(1);
        }

        public static unsafe void AddPublicKey(Span<byte> a, Span<byte> b)
        {
            Span<byte> blsA = stackalloc byte[BlsPublicKeyLength];
            Span<byte> blsB = stackalloc byte[BlsPublicKeyLength];
            fixed (byte* aRef = a)
            fixed (byte* bRef = b)
            fixed (byte* blsARef = blsA)
            fixed (byte* blsBRef = blsB)
            {
                int read1 = blsPublicKeyDeserialize(blsARef, aRef, PublicKeyLength);
                int read2 = blsPublicKeyDeserialize(blsBRef, bRef, PublicKeyLength);
                blsPublicKeyAdd(blsARef, blsBRef);
                int written1 = blsPublicKeySerialize(aRef, PublicKeyLength, blsARef);
            }
        }
        
        public static unsafe void AddSignature(Span<byte> a, Span<byte> b)
        {
            Span<byte> blsA = stackalloc byte[BlsSignatureLength];
            Span<byte> blsB = stackalloc byte[BlsSignatureLength];
            fixed (byte* aRef = a)
            fixed (byte* bRef = b)
            fixed (byte* blsARef = blsA)
            fixed (byte* blsBRef = blsB)
            {
                int read1 = blsSignatureDeserialize(blsARef, aRef, SignatureLength);
                int read2 = blsSignatureDeserialize(blsBRef, bRef, SignatureLength);
                blsSignatureAdd(blsARef, blsBRef);
                int written1 = blsSignatureSerialize(aRef, SignatureLength, blsARef);
            }
        }

        public static unsafe void GetPublicKey(Span<byte> privateKeyBytes, out Span<byte> publicKeyBytes)
        {
            Span<byte> blsPrivateKey = stackalloc byte[BlsPrivateKeyLength];
            Span<byte> blsPublicKey = stackalloc byte[BlsPublicKeyLength];
            publicKeyBytes = new byte[PublicKeyLength];

            fixed (byte* privateKeyBytesRef = privateKeyBytes)
            fixed (byte* publicKeyBytesRef = publicKeyBytes)
            fixed (byte* blsPrivateKeyRef = blsPrivateKey)
            fixed (byte* blsPublicKeyRef = blsPublicKey)
            {
                int bytesRead = blsSecretKeyDeserialize(blsPrivateKeyRef, privateKeyBytesRef, PrivateKeyLength);
                if (bytesRead != PrivateKeyLength)
                {
                    throw new CryptographicException($"Bytes read was {bytesRead} when deserializing private key");
                }

                blsGetPublicKey(blsPublicKeyRef, blsPrivateKeyRef);
                int bytesWritten = blsPublicKeySerialize(publicKeyBytesRef, PublicKeyLength, blsPublicKeyRef);
                if (bytesWritten != PublicKeyLength)
                {
                    throw new CryptographicException($"Bytes written was {bytesWritten} when serializing private key");
                }
            }
        }
        
        public static unsafe void HashWithDomain(out Span<byte> signatureBytes, out Span<byte> blsSignatureBytes, Span<byte> hashBytes, Span<byte> domainBytes)
        {
            blsSignatureBytes = new byte[BlsSignatureLength];
            signatureBytes = new byte[SignatureLength];
            
            Span<byte> hashWithDomain = stackalloc byte[HashLength + DomainLength];
            hashBytes.CopyTo(hashWithDomain.Slice(0, 32));
            domainBytes.CopyTo(hashWithDomain.Slice(32, 8));
            
            fixed (byte* signatureBytesRef = signatureBytes)
            fixed (byte* blsSignatureBytesRef = blsSignatureBytes)
            fixed (byte* hashWithDomainRef = hashWithDomain)
            {
                blsHashWithDomainToFp2(signatureBytesRef, hashWithDomainRef);
                int bytesRead = blsSignatureDeserialize(blsSignatureBytesRef, signatureBytesRef, SignatureLength);
//                if (bytesRead != SignatureLength)
//                {
//                    throw new CryptographicException($"Bytes read was {bytesRead} when deserializing signature");
//                }
            }
        }
        
        public static unsafe void Sign(out Span<byte> signatureBytes, Span<byte> privateKeyBytes, Span<byte> hashBytes, Span<byte> domainBytes)
        {
            Span<byte> blsPrivateKeyBytes = stackalloc byte[BlsPrivateKeyLength];
            Span<byte> blsSignatureBytes = stackalloc byte[BlsSignatureLength];
            signatureBytes = new byte[SignatureLength];
            
            Span<byte> hashWithDomain = stackalloc byte[HashLength + DomainLength];
            hashBytes.CopyTo(hashWithDomain.Slice(0, 32));
            domainBytes.CopyTo(hashWithDomain.Slice(32, 8));

            fixed (byte* privateKeyBytesRef = privateKeyBytes)
            fixed (byte* blsPrivateKeyRef = blsPrivateKeyBytes)
            fixed (byte* signatureBytesRef = signatureBytes)
            fixed (byte* blsSignatureBytesRef = blsSignatureBytes)
            fixed (byte* hashWithDomainRef = hashWithDomain)
            {
                int bytesRead = blsSecretKeyDeserialize(blsPrivateKeyRef, privateKeyBytesRef, PrivateKeyLength);
                if (bytesRead != PrivateKeyLength)
                {
                    throw new CryptographicException($"Bytes read was {bytesRead} when deserializing private key");
                }

                blsSignHashWithDomain(blsSignatureBytesRef, blsPrivateKeyRef, hashWithDomainRef);
                int bytesWritten = blsSignatureSerialize(signatureBytesRef, SignatureLength, blsSignatureBytesRef);
                if (bytesWritten != SignatureLength)
                {
                    throw new CryptographicException($"Bytes read was {bytesRead} when deserializing signature");
                }
            }
        }

        [DllImport("bls384_256.dll")]
        private static extern unsafe void blsGetPublicKey(byte* blsPublicKey, byte* blsPrivateKey);

        [DllImport("bls384_256.dll")]
        private static extern int blsInit(int curveId, int compiledTimeVar);

        [DllImport("bls384_256.dll")]
        private static extern unsafe void blsPublicKeyAdd(byte* blsPublicKeyA, byte* blsPublicKeyB);

        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsPublicKeyDeserialize(byte* blsPublicKey, byte* publicKeyBytes, int publicKeyLength);

        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsPublicKeySerialize(byte* publicKeyBytes, int publicKeyLength, byte* blsPublicKey);

        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsSecretKeyDeserialize(byte* blsPrivateKey, byte* privateKeyBytes, int privateKeyLength);

        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsSecretKeySerialize(byte* privateKeyBytes, int privateKeyLength, byte* blsPrivateKey);

        [DllImport("bls384_256.dll")]
        private static extern void blsSetETHserialization(int ETHserialization);

        [DllImport("bls384_256.dll")]
        private static extern unsafe void blsHashWithDomainToFp2(byte* signature, byte* hashWithDomain);
        
        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsSignHash(byte* blsSignature, byte* blsPrivateKey, byte* hash, int size);
        
        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsSignHashWithDomain(byte* blsSignature, byte* blsPrivateKey, byte* hashWithDomain);

        [DllImport("bls384_256.dll")]
        private static extern unsafe void blsSignatureAdd(byte* blsSignatureA, byte* blsSignatureB);

        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsSignatureDeserialize(byte* blsSignatureBytes, byte* signatureBytes, int bufferSize);

        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsSignatureSerialize(byte* signatureBytes, int signatureLength, byte* blsSignature);

        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsVerifyHash(byte* blsSignature, byte* blsPublicKey, byte* hash, int size);
        
        [DllImport("bls384_256.dll")]
        private static extern unsafe int blsVerifyHashWithDomain(byte* blsSignature, byte* blsPublicKey, byte* hashWithDomain);
    }
}