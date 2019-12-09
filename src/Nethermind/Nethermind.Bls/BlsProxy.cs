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

        private enum OsPlatform
        {
            Windows,
            Linux,
            Mac
        }

        private static readonly OsPlatform Platform;

        static BlsProxy()
        {
            Platform = GetPlatform();
            
            int initResult = Platform switch
            {
                OsPlatform.Windows => Win64Lib.blsInit(MclBls12_381CurveId, MclBnCompileTimeVar),
                OsPlatform.Linux => PosixLib.blsInit(MclBls12_381CurveId, MclBnCompileTimeVar),
                OsPlatform.Mac => MacLib.blsInit(MclBls12_381CurveId, MclBnCompileTimeVar),
                _ => throw new ArgumentOutOfRangeException(Platform.ToString())
            };

            if (initResult != 0)
            {
                throw new CryptographicException($"Unable to load the BLS lib: {initResult}");
            }

            switch (Platform)
            {
                case OsPlatform.Windows:
                    Win64Lib.blsSetETHserialization(1);
                    break;
                case OsPlatform.Mac:
                    MacLib.blsSetETHserialization(1);
                    break;
                case OsPlatform.Linux:
                    PosixLib.blsSetETHserialization(1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(Platform.ToString());
            }
        }

        private static OsPlatform GetPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return OsPlatform.Windows;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return OsPlatform.Linux;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return OsPlatform.Mac;
            }

            throw new InvalidOperationException("Unsupported platform.");
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
                DeserializePublicKey(blsARef, aRef);
                DeserializePublicKey(blsBRef, bRef);
                
                switch (Platform)
                {
                    case OsPlatform.Windows:
                        Win64Lib.blsPublicKeyAdd(blsARef, blsBRef);
                        break;
                    case OsPlatform.Mac:
                        MacLib.blsPublicKeyAdd(blsARef, blsBRef);
                        break;
                    case OsPlatform.Linux:
                        PosixLib.blsPublicKeyAdd(blsARef, blsBRef);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(Platform.ToString());
                }

                SerializePublicKey(aRef, blsARef);
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
                DeserializeSignature(blsARef, aRef);
                DeserializeSignature(blsBRef, bRef);

                switch (Platform)
                {
                    case OsPlatform.Windows:
                        Win64Lib.blsSignatureAdd(blsARef, blsBRef);
                        break;
                    case OsPlatform.Mac:
                        MacLib.blsSignatureAdd(blsARef, blsBRef);
                        break;
                    case OsPlatform.Linux:
                        PosixLib.blsSignatureAdd(blsARef, blsBRef);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(Platform.ToString());
                }

                SerializeSignature(aRef, blsARef);
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
                DeserializePrivateKey(blsPrivateKeyRef, privateKeyBytesRef);

                switch (Platform)
                {
                    case OsPlatform.Windows:
                        Win64Lib.blsGetPublicKey(blsPublicKeyRef, blsPrivateKeyRef);
                        break;
                    case OsPlatform.Mac:
                        MacLib.blsGetPublicKey(blsPublicKeyRef, blsPrivateKeyRef);
                        break;
                    case OsPlatform.Linux:
                        PosixLib.blsGetPublicKey(blsPublicKeyRef, blsPrivateKeyRef);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(Platform.ToString());
                }

                SerializePublicKey(publicKeyBytesRef, blsPublicKeyRef);
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
                switch (Platform)
                {
                    case OsPlatform.Windows:
                        Win64Lib.blsHashWithDomainToFp2(signatureBytesRef, hashWithDomainRef);
                        break;
                    case OsPlatform.Mac:
                        MacLib.blsHashWithDomainToFp2(signatureBytesRef, hashWithDomainRef);
                        break;
                    case OsPlatform.Linux:
                        PosixLib.blsHashWithDomainToFp2(signatureBytesRef, hashWithDomainRef);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(Platform.ToString());
                }

                DeserializeSignature(blsSignatureBytesRef, signatureBytesRef);
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
                DeserializePrivateKey(blsPrivateKeyRef, privateKeyBytesRef);

                switch (Platform)
                {
                    case OsPlatform.Windows:
                        Win64Lib.blsSignHashWithDomain(blsSignatureBytesRef, blsPrivateKeyRef, hashWithDomainRef);
                        break;
                    case OsPlatform.Mac:
                        MacLib.blsSignHashWithDomain(blsSignatureBytesRef, blsPrivateKeyRef, hashWithDomainRef);
                        break;
                    case OsPlatform.Linux:
                        PosixLib.blsSignHashWithDomain(blsSignatureBytesRef, blsPrivateKeyRef, hashWithDomainRef);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(Platform.ToString());
                }

                SerializeSignature(signatureBytesRef, blsSignatureBytesRef);
            }
        }

        private static unsafe void DeserializeSignature(byte* deserializedRef, byte* serializedRef)
        {
            int bytesRead = Platform switch
            {
                OsPlatform.Windows => Win64Lib.blsSignatureDeserialize(deserializedRef, serializedRef, SignatureLength),
                OsPlatform.Linux => PosixLib.blsSignatureDeserialize(deserializedRef, serializedRef, SignatureLength),
                OsPlatform.Mac => MacLib.blsSignatureDeserialize(deserializedRef, serializedRef, SignatureLength),
                _ => throw new ArgumentOutOfRangeException(Platform.ToString())
            };
            
            if (bytesRead != SignatureLength)
            {
                throw new CryptographicException($"Bytes read was {bytesRead} instead of {SignatureLength} when deserializing signature");
            }
        }
        
        private static unsafe void SerializeSignature(byte* serializedRef, byte* deserializedRef)
        {
            int bytesWritten = Platform switch
            {
                OsPlatform.Windows => Win64Lib.blsSignatureSerialize(serializedRef, SignatureLength, deserializedRef),
                OsPlatform.Linux => PosixLib.blsSignatureSerialize(serializedRef, SignatureLength, deserializedRef),
                OsPlatform.Mac => MacLib.blsSignatureSerialize(serializedRef, SignatureLength, deserializedRef),
                _ => throw new ArgumentOutOfRangeException(Platform.ToString())
            };
            
            if (bytesWritten != SignatureLength)
            {
                throw new CryptographicException($"Bytes written was {bytesWritten} instead of {SignatureLength} when deserializing private key");
            }
        }
        
        private static unsafe void DeserializePublicKey(byte* deserializedRef, byte* serializedRef)
        {
            int bytesRead = Platform switch
            {
                OsPlatform.Windows => Win64Lib.blsPublicKeyDeserialize(deserializedRef, serializedRef, PublicKeyLength),
                OsPlatform.Linux => PosixLib.blsPublicKeyDeserialize(deserializedRef, serializedRef, PublicKeyLength),
                OsPlatform.Mac => MacLib.blsPublicKeyDeserialize(deserializedRef, serializedRef, PublicKeyLength),
                _ => throw new ArgumentOutOfRangeException(Platform.ToString())
            };
            
            if (bytesRead != PublicKeyLength)
            {
                throw new CryptographicException($"Bytes read was {bytesRead} instead of {PublicKeyLength} when deserializing private key");
            }
        }
        
        private static unsafe void SerializePublicKey(byte* serializedRef, byte* deserializedRef)
        {
            int bytesWritten = Platform switch
            {
                OsPlatform.Windows => Win64Lib.blsPublicKeySerialize(serializedRef, PublicKeyLength, deserializedRef),
                OsPlatform.Linux => PosixLib.blsPublicKeySerialize(serializedRef, PublicKeyLength, deserializedRef),
                OsPlatform.Mac => MacLib.blsPublicKeySerialize(serializedRef, PublicKeyLength, deserializedRef),
                _ => throw new ArgumentOutOfRangeException(Platform.ToString())
            };

            if (bytesWritten != PublicKeyLength)
            {
                throw new CryptographicException($"Bytes written was {bytesWritten} when serializing public key");
            }
        }
        
        private static unsafe void DeserializePrivateKey(byte* deserializedRef, byte* serializedRef)
        {
            int bytesRead = Platform switch
            {
                OsPlatform.Windows => Win64Lib.blsSecretKeyDeserialize(deserializedRef, serializedRef, PrivateKeyLength),
                OsPlatform.Linux => PosixLib.blsSecretKeyDeserialize(deserializedRef, serializedRef, PrivateKeyLength),
                OsPlatform.Mac => MacLib.blsSecretKeyDeserialize(deserializedRef, serializedRef, PrivateKeyLength),
                _ => throw new ArgumentOutOfRangeException(Platform.ToString())
            };
            
            if (bytesRead != PrivateKeyLength)
            {
                throw new CryptographicException($"Bytes read was {bytesRead} instead of {PrivateKeyLength} when deserializing private key");
            }
        }

        private static class Win64Lib
        {
            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe void blsGetPublicKey(byte* blsPublicKey, byte* blsPrivateKey);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern int blsInit(int curveId, int compiledTimeVar);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe void blsPublicKeyAdd(byte* blsPublicKeyA, byte* blsPublicKeyB);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsPublicKeyDeserialize(byte* blsPublicKey, byte* publicKeyBytes, int publicKeyLength);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsPublicKeySerialize(byte* publicKeyBytes, int publicKeyLength, byte* blsPublicKey);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsSecretKeyDeserialize(byte* blsPrivateKey, byte* privateKeyBytes, int privateKeyLength);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsSecretKeySerialize(byte* privateKeyBytes, int privateKeyLength, byte* blsPrivateKey);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern void blsSetETHserialization(int ETHserialization);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe void blsHashWithDomainToFp2(byte* signature, byte* hashWithDomain);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsSignHash(byte* blsSignature, byte* blsPrivateKey, byte* hash, int size);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsSignHashWithDomain(byte* blsSignature, byte* blsPrivateKey, byte* hashWithDomain);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe void blsSignatureAdd(byte* blsSignatureA, byte* blsSignatureB);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsSignatureDeserialize(byte* blsSignatureBytes, byte* signatureBytes, int bufferSize);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsSignatureSerialize(byte* signatureBytes, int signatureLength, byte* blsSignature);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsVerifyHash(byte* blsSignature, byte* blsPublicKey, byte* hash, int size);

            [DllImport("runtimes\\win-x64\\native\\bls384_256.dll")]
            public static extern unsafe int blsVerifyHashWithDomain(byte* blsSignature, byte* blsPublicKey, byte* hashWithDomain);
        }

        private static class PosixLib
        {
            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe void blsGetPublicKey(byte* blsPublicKey, byte* blsPrivateKey);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern int blsInit(int curveId, int compiledTimeVar);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe void blsPublicKeyAdd(byte* blsPublicKeyA, byte* blsPublicKeyB);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsPublicKeyDeserialize(byte* blsPublicKey, byte* publicKeyBytes, int publicKeyLength);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsPublicKeySerialize(byte* publicKeyBytes, int publicKeyLength, byte* blsPublicKey);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsSecretKeyDeserialize(byte* blsPrivateKey, byte* privateKeyBytes, int privateKeyLength);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsSecretKeySerialize(byte* privateKeyBytes, int privateKeyLength, byte* blsPrivateKey);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern void blsSetETHserialization(int ETHserialization);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe void blsHashWithDomainToFp2(byte* signature, byte* hashWithDomain);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsSignHash(byte* blsSignature, byte* blsPrivateKey, byte* hash, int size);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsSignHashWithDomain(byte* blsSignature, byte* blsPrivateKey, byte* hashWithDomain);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe void blsSignatureAdd(byte* blsSignatureA, byte* blsSignatureB);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsSignatureDeserialize(byte* blsSignatureBytes, byte* signatureBytes, int bufferSize);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsSignatureSerialize(byte* signatureBytes, int signatureLength, byte* blsSignature);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsVerifyHash(byte* blsSignature, byte* blsPublicKey, byte* hash, int size);

            [DllImport("runtimes\\linux-x64\\native\\libbls384_256.so")]
            public static extern unsafe int blsVerifyHashWithDomain(byte* blsSignature, byte* blsPublicKey, byte* hashWithDomain);
        }

        private static class MacLib
        {
            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe void blsGetPublicKey(byte* blsPublicKey, byte* blsPrivateKey);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern int blsInit(int curveId, int compiledTimeVar);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe void blsPublicKeyAdd(byte* blsPublicKeyA, byte* blsPublicKeyB);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsPublicKeyDeserialize(byte* blsPublicKey, byte* publicKeyBytes, int publicKeyLength);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsPublicKeySerialize(byte* publicKeyBytes, int publicKeyLength, byte* blsPublicKey);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsSecretKeyDeserialize(byte* blsPrivateKey, byte* privateKeyBytes, int privateKeyLength);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsSecretKeySerialize(byte* privateKeyBytes, int privateKeyLength, byte* blsPrivateKey);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern void blsSetETHserialization(int ETHserialization);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe void blsHashWithDomainToFp2(byte* signature, byte* hashWithDomain);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsSignHash(byte* blsSignature, byte* blsPrivateKey, byte* hash, int size);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsSignHashWithDomain(byte* blsSignature, byte* blsPrivateKey, byte* hashWithDomain);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe void blsSignatureAdd(byte* blsSignatureA, byte* blsSignatureB);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsSignatureDeserialize(byte* blsSignatureBytes, byte* signatureBytes, int bufferSize);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsSignatureSerialize(byte* signatureBytes, int signatureLength, byte* blsSignature);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsVerifyHash(byte* blsSignature, byte* blsPublicKey, byte* hash, int size);

            [DllImport("runtimes\\osx-x64\\native\\bls384_256.dylib")]
            public static extern unsafe int blsVerifyHashWithDomain(byte* blsSignature, byte* blsPublicKey, byte* hashWithDomain);
        }
    }
}