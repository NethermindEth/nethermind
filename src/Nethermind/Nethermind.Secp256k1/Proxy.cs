/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

/* ECDH bindings were based on ECDH bindings from Secp256k1.Net (MIT license) */

using System;
using System.Runtime.InteropServices;
using System.Security;

namespace Nethermind.Secp256k1
{
    public static class Proxy
    {
        // TODO: there was some work planned with .NET Core team to allow to map libraries based on the system in DllImport
        private static class Win64Lib
        {
            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern /* secp256k1_context */ IntPtr secp256k1_context_create(uint flags);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern /* void */ IntPtr secp256k1_context_destroy(IntPtr context);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ec_seckey_verify( /* secp256k1_context */ IntPtr context, byte[] seckey);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ec_pubkey_create( /* secp256k1_context */ IntPtr context, byte[] pubkey, byte[] seckey);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ec_pubkey_serialize( /* secp256k1_context */ IntPtr context, byte[] serializedPublicKey, ref uint outputSize, byte[] publicKey, uint flags);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ecdsa_sign_recoverable( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] messageHash, byte[] privateKey, IntPtr nonceFunction, IntPtr nonceData);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ecdsa_recoverable_signature_serialize_compact( /* secp256k1_context */ IntPtr context, byte[] compactSignature, out int recoveryId, byte[] signature);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ecdsa_recoverable_signature_parse_compact( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] compactSignature, int recoveryId);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ecdsa_recover( /* secp256k1_context */ IntPtr context, byte[] publicKey, byte[] signature, byte[] message);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern bool secp256k1_ecdh( /* secp256k1_context */ IntPtr context, byte[] output, byte[] publicKey, byte[] privateKey, IntPtr hashFunctionPointer, IntPtr data);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern unsafe int secp256k1_ec_pubkey_parse(IntPtr ctx, void* pubkey, void* input, uint inputlen);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dll")]
            public static extern unsafe int secp256k1_ec_pubkey_serialize(IntPtr ctx, void* output, ref uint outputlen, void* pubkey, uint flags);
        }

        private static class PosixLib
        {
            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern /* secp256k1_context */ IntPtr secp256k1_context_create(uint flags);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern /* void */ IntPtr secp256k1_context_destroy(IntPtr context);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ec_seckey_verify( /* secp256k1_context */ IntPtr context, byte[] seckey);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ec_pubkey_create( /* secp256k1_context */ IntPtr context, byte[] pubkey, byte[] seckey);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ec_pubkey_serialize( /* secp256k1_context */ IntPtr context, byte[] serializedPublicKey, ref uint outputSize, byte[] publicKey, uint flags);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ecdsa_sign_recoverable( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] messageHash, byte[] privateKey, IntPtr nonceFunction, IntPtr nonceData);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ecdsa_recoverable_signature_serialize_compact( /* secp256k1_context */ IntPtr context, byte[] compactSignature, out int recoveryId, byte[] signature);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ecdsa_recoverable_signature_parse_compact( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] compactSignature, int recoveryId);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ecdsa_recover( /* secp256k1_context */ IntPtr context, byte[] publicKey, byte[] signature, byte[] message);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern bool secp256k1_ecdh( /* secp256k1_context */ IntPtr context, byte[] output, byte[] publicKey, byte[] privateKey, IntPtr hashFunctionPointer, IntPtr data);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern unsafe int secp256k1_ec_pubkey_parse(IntPtr ctx, void* pubkey, void* input, uint inputlen);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.so")]
            public static extern unsafe int secp256k1_ec_pubkey_serialize(IntPtr ctx, void* output, ref uint outputlen, void* pubkey, uint flags);
        }

        private static class MacLib
        {
            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern /* secp256k1_context */ IntPtr secp256k1_context_create(uint flags);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern /* void */ IntPtr secp256k1_context_destroy(IntPtr context);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ec_seckey_verify( /* secp256k1_context */ IntPtr context, byte[] seckey);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ec_pubkey_create( /* secp256k1_context */ IntPtr context, byte[] pubkey, byte[] seckey);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ec_pubkey_serialize( /* secp256k1_context */ IntPtr context, byte[] serializedPublicKey, ref uint outputSize, byte[] publicKey, uint flags);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ecdsa_sign_recoverable( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] messageHash, byte[] privateKey, IntPtr nonceFunction, IntPtr nonceData);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ecdsa_recoverable_signature_serialize_compact( /* secp256k1_context */ IntPtr context, byte[] compactSignature, out int recoveryId, byte[] signature);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ecdsa_recoverable_signature_parse_compact( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] compactSignature, int recoveryId);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ecdsa_recover( /* secp256k1_context */ IntPtr context, byte[] publicKey, byte[] signature, byte[] message);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern bool secp256k1_ecdh( /* secp256k1_context */ IntPtr context, byte[] output, byte[] publicKey, byte[] privateKey, IntPtr hashFunctionPointer, IntPtr data);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern unsafe int secp256k1_ec_pubkey_parse(IntPtr ctx, void* pubkey, void* input, uint inputlen);

            [SuppressUnmanagedCodeSecurity]
            [DllImport("secp256k1.dylib")]
            public static extern unsafe int secp256k1_ec_pubkey_serialize(IntPtr ctx, void* output, ref uint outputlen, void* pubkey, uint flags);
        }

        /* constants from pycoin (https://github.com/richardkiss/pycoin)*/
        private const uint Secp256K1FlagsTypeMask = (1 << 8) - 1;

        private const uint Secp256K1FlagsTypeContext = 1 << 0;

        private const uint Secp256K1FlagsTypeCompression = 1 << 1;

        /* The higher bits contain the actual data. Do not use directly. */
        private const uint Secp256K1FlagsBitContextVerify = 1 << 8;

        private const uint Secp256K1FlagsBitContextSign = 1 << 9;
        private const uint Secp256K1FlagsBitCompression = 1 << 8;

        /* Flags to pass to secp256k1_context_create. */
        private const uint Secp256K1ContextVerify = Secp256K1FlagsTypeContext | Secp256K1FlagsBitContextVerify;

        private const uint Secp256K1ContextSign = Secp256K1FlagsTypeContext | Secp256K1FlagsBitContextSign;
        private const uint Secp256K1ContextNone = Secp256K1FlagsTypeContext;

        private const uint Secp256K1EcCompressed = Secp256K1FlagsTypeCompression | Secp256K1FlagsBitCompression;
        private const uint Secp256K1EcUncompressed = Secp256K1FlagsTypeCompression;

        private static readonly OsPlatform Platform;
        private static readonly IntPtr Context;

        private enum OsPlatform
        {
            Windows,
            Linux,
            Mac
        }

        static Proxy()
        {
            Platform = GetPlatform();
            Context = CreateContext();
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

        private static IntPtr CreateContext()
        {
            switch (Platform)
            {
                case OsPlatform.Windows:
                    return Win64Lib.secp256k1_context_create(Secp256K1ContextSign | Secp256K1ContextVerify);
                case OsPlatform.Linux:
                    return PosixLib.secp256k1_context_create(Secp256K1ContextSign | Secp256K1ContextVerify);
                case OsPlatform.Mac:
                    return MacLib.secp256k1_context_create(Secp256K1ContextSign | Secp256K1ContextVerify);
            }

            throw new InvalidOperationException("Unsupported platform.");
        }

        public static bool VerifyPrivateKey(byte[] privateKey)
        {
            switch (Platform)
            {
                case OsPlatform.Windows:
                    return Win64Lib.secp256k1_ec_seckey_verify(Context, privateKey);
                case OsPlatform.Linux:
                    return PosixLib.secp256k1_ec_seckey_verify(Context, privateKey);
                case OsPlatform.Mac:
                    return MacLib.secp256k1_ec_seckey_verify(Context, privateKey);
            }

            throw new InvalidOperationException("Unsupported platform.");
        }

        public static byte[] GetPublicKey(byte[] privateKey, bool compressed)
        {
            byte[] publicKey = new byte[64];
            if (Platform == OsPlatform.Windows
                ? !Win64Lib.secp256k1_ec_pubkey_create(Context, publicKey, privateKey)
                : !(Platform == OsPlatform.Linux
                    ? PosixLib.secp256k1_ec_pubkey_create(Context, publicKey, privateKey)
                    : MacLib.secp256k1_ec_pubkey_create(Context, publicKey, privateKey)))
            {
                return null;
            }

            byte[] serializedPublicKey = new byte[compressed ? 33 : 65];
            uint outputSize = (uint) serializedPublicKey.Length;
            uint flags = compressed ? Secp256K1EcCompressed : Secp256K1EcUncompressed;
            if (Platform == OsPlatform.Windows
                ? !Win64Lib.secp256k1_ec_pubkey_serialize(Context, serializedPublicKey, ref outputSize, publicKey, flags)
                : !(Platform == OsPlatform.Linux
                    ? PosixLib.secp256k1_ec_pubkey_serialize(Context, serializedPublicKey, ref outputSize, publicKey, flags)
                    : MacLib.secp256k1_ec_pubkey_serialize(Context, serializedPublicKey, ref outputSize, publicKey, flags)))
            {
                return null;
            }

            return serializedPublicKey;
        }

        public static byte[] SignCompact(byte[] messageHash, byte[] privateKey, out int recoveryId)
        {
            byte[] recoverableSignature = new byte[65];
            recoveryId = 0;

            if (Platform == OsPlatform.Windows
                ? !Win64Lib.secp256k1_ecdsa_sign_recoverable(Context, recoverableSignature, messageHash, privateKey, IntPtr.Zero, IntPtr.Zero)
                : !(Platform == OsPlatform.Linux
                    ? PosixLib.secp256k1_ecdsa_sign_recoverable(Context, recoverableSignature, messageHash, privateKey, IntPtr.Zero, IntPtr.Zero)
                    : MacLib.secp256k1_ecdsa_sign_recoverable(Context, recoverableSignature, messageHash, privateKey, IntPtr.Zero, IntPtr.Zero)))
            {
                return null;
            }

            byte[] compactSignature = new byte[64];
            if (Platform == OsPlatform.Windows
                ? !Win64Lib.secp256k1_ecdsa_recoverable_signature_serialize_compact(Context, compactSignature, out recoveryId, recoverableSignature)
                : !(Platform == OsPlatform.Linux
                    ? PosixLib.secp256k1_ecdsa_recoverable_signature_serialize_compact(Context, compactSignature, out recoveryId, recoverableSignature)
                    : MacLib.secp256k1_ecdsa_recoverable_signature_serialize_compact(Context, compactSignature, out recoveryId, recoverableSignature)))
            {
                return null;
            }

            return compactSignature;
        }

        public static byte[] RecoverKeyFromCompact(byte[] messageHash, byte[] compactSignature, int recoveryId, bool compressed)
        {
            byte[] recoverableSignature = new byte[65];

            if (Platform == OsPlatform.Windows
                ? !Win64Lib.secp256k1_ecdsa_recoverable_signature_parse_compact(Context, recoverableSignature, compactSignature, recoveryId)
                : !(Platform == OsPlatform.Linux
                    ? PosixLib.secp256k1_ecdsa_recoverable_signature_parse_compact(Context, recoverableSignature, compactSignature, recoveryId)
                    : MacLib.secp256k1_ecdsa_recoverable_signature_parse_compact(Context, recoverableSignature, compactSignature, recoveryId)))
            {
                return null;
            }

            byte[] publicKey = new byte[64];
            if (Platform == OsPlatform.Windows
                ? !Win64Lib.secp256k1_ecdsa_recover(Context, publicKey, recoverableSignature, messageHash)
                : !(Platform == OsPlatform.Linux
                    ? PosixLib.secp256k1_ecdsa_recover(Context, publicKey, recoverableSignature, messageHash)
                    : MacLib.secp256k1_ecdsa_recover(Context, publicKey, recoverableSignature, messageHash)))
            {
                return null;
            }

            uint flags = compressed ? Secp256K1EcCompressed : Secp256K1EcUncompressed;
            byte[] serializedPublicKey = new byte[compressed ? 33 : 65];
            uint outputSize = (uint) serializedPublicKey.Length;
            if (Platform == OsPlatform.Windows
                ? !Win64Lib.secp256k1_ec_pubkey_serialize(Context, serializedPublicKey, ref outputSize, publicKey, flags)
                : !(Platform == OsPlatform.Linux
                    ? PosixLib.secp256k1_ec_pubkey_serialize(Context, serializedPublicKey, ref outputSize, publicKey, flags)
                    : MacLib.secp256k1_ec_pubkey_serialize(Context, serializedPublicKey, ref outputSize, publicKey, flags)))
            {
                return null;
            }

            return serializedPublicKey;
        }

        unsafe delegate int secp256k1_ecdh_hash_function(void* output, void* x, void* y, IntPtr data);

        public static unsafe bool Ecdh(byte[] agreement, byte[] publicKey, byte[] privateKey)
        {
            int outputLength = agreement.Length;

            secp256k1_ecdh_hash_function hashFunctionPtr = (void* output, void* x, void* y, IntPtr d) =>
            {
                var outputSpan = new Span<byte>(output, outputLength);
                var xSpan = new Span<byte>(x, 32);
                if (xSpan.Length < 32)
                {
                    return 0;
                }

                xSpan.CopyTo(outputSpan);
                return 1;
            };

            GCHandle gch = GCHandle.Alloc(hashFunctionPtr);
            try
            {
                IntPtr fp = Marshal.GetFunctionPointerForDelegate(hashFunctionPtr);
                {
                    return Platform == OsPlatform.Windows
                        ? Win64Lib.secp256k1_ecdh(Context, agreement, publicKey, privateKey, fp, IntPtr.Zero)
                        : Platform == OsPlatform.Linux
                            ? PosixLib.secp256k1_ecdh(Context, agreement, publicKey, privateKey, fp, IntPtr.Zero)
                            : MacLib.secp256k1_ecdh(Context, agreement, publicKey, privateKey, fp, IntPtr.Zero);
                }
            }
            finally
            {
                gch.Free();
            }
        }

        public static byte[] EcdhSerialized(byte[] publicKey, byte[] privateKey)
        {
            Span<byte> serializedKey = stackalloc byte[65];
            ToPublicKeyArray(serializedKey, publicKey);
            byte[] key = new byte[64];
            PublicKeyParse(key, serializedKey);
            byte[] result = new byte[32];
            Ecdh(result, key, privateKey);
            return result;
        }

        /// <summary>
        /// Parse a variable-length public key into the pubkey object.
        /// This function supports parsing compressed (33 bytes, header byte 0x02 or
        /// 0x03), uncompressed(65 bytes, header byte 0x04), or hybrid(65 bytes, header
        /// byte 0x06 or 0x07) format public keys.
        /// </summary>
        /// <param name="publicKeyOutput">(Output) pointer to a pubkey object. If 1 is returned, it is set to a parsed version of input. If not, its value is undefined.</param>
        /// <param name="serializedPublicKey">Serialized public key.</param>
        /// <returns>True if the public key was fully valid, false if the public key could not be parsed or is invalid.</returns>
        public static unsafe bool PublicKeyParse(Span<byte> publicKeyOutput, Span<byte> serializedPublicKey)
        {
            var inputLen = serializedPublicKey.Length;
            if (inputLen != 33 && inputLen != 65)
            {
                throw new ArgumentException($"{nameof(serializedPublicKey)} must be 33 or 65 bytes");
            }

            if (publicKeyOutput.Length < 64)
            {
                throw new ArgumentException($"{nameof(publicKeyOutput)} must be {64} bytes");
            }

            fixed (byte* pubKeyPtr = &MemoryMarshal.GetReference(publicKeyOutput),
                serializedPtr = &MemoryMarshal.GetReference(serializedPublicKey))
            {
                return (Platform == OsPlatform.Windows
                    ? Win64Lib.secp256k1_ec_pubkey_parse(Context, pubKeyPtr, serializedPtr, (uint) inputLen)
                    : Platform == OsPlatform.Linux
                        ? PosixLib.secp256k1_ec_pubkey_parse(Context, pubKeyPtr, serializedPtr, (uint) inputLen)
                        : MacLib.secp256k1_ec_pubkey_parse(Context, pubKeyPtr, serializedPtr, (uint) inputLen)) == 1;
            }
        }
        
        /// <summary>
        /// Serialize a pubkey object into a serialized byte sequence.
        /// </summary>
        /// <param name="serializedPublicKeyOutput">65-byte (if compressed==0) or 33-byte (if compressed==1) output to place the serialized key in.</param>
        /// <param name="publicKey">The secp256k1_pubkey initialized public key.</param>
        /// <param name="flags">SECP256K1_EC_COMPRESSED if serialization should be in compressed format, otherwise SECP256K1_EC_UNCOMPRESSED.</param>
        public static unsafe bool PublicKeySerialize(Span<byte> serializedPublicKeyOutput, Span<byte> publicKey, uint flags = Secp256K1EcUncompressed)
        {
            bool compressed = (flags & Secp256K1EcCompressed) == Secp256K1EcCompressed;
            int serializedPubKeyLength = compressed ? 33 : 65;
            if (serializedPublicKeyOutput.Length < serializedPubKeyLength)
            {
                string compressedStr = compressed ? "compressed" : "uncompressed";
                throw new ArgumentException($"{nameof(serializedPublicKeyOutput)} ({compressedStr}) must be {serializedPubKeyLength} bytes");
            }
            if (publicKey.Length < 64)
            {
                throw new ArgumentException($"{nameof(publicKey)} must be {64} bytes");
            }

            uint newLength = (uint)serializedPubKeyLength;

            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(serializedPublicKeyOutput),
                pubKeyPtr = &MemoryMarshal.GetReference(publicKey))
            {
                var result = (Platform == OsPlatform.Windows
                           ? Win64Lib.secp256k1_ec_pubkey_serialize(Context, serializedPtr, ref newLength, pubKeyPtr, (uint) flags)
                           : Platform == OsPlatform.Linux
                               ? PosixLib.secp256k1_ec_pubkey_serialize(Context, serializedPtr, ref newLength, pubKeyPtr, (uint) flags)
                               : MacLib.secp256k1_ec_pubkey_serialize(Context, serializedPtr, ref newLength, pubKeyPtr, (uint) flags));
                
                return result == 1 && newLength == serializedPubKeyLength;
            }
        }

        private static void ToPublicKeyArray(Span<byte> serializedKey, byte[] unmanaged)
        {
            // Define the public key array
            Span<byte> publicKey = stackalloc byte[64];

            // Add our uncompressed prefix to our key.
            Span<byte> uncompressedPrefixedPublicKey = stackalloc byte[65];
            uncompressedPrefixedPublicKey[0] = 4;
            unmanaged.AsSpan().CopyTo(uncompressedPrefixedPublicKey.Slice(1));

            // Parse our public key from the serialized data.
            if (!PublicKeyParse(publicKey, uncompressedPrefixedPublicKey))
            {
                var errMsg = "Unmanaged EC library failed to deserialize public key. ";
                throw new Exception(errMsg);
            }

            // Serialize the public key
            uint serializedKeyFlags = Secp256K1EcUncompressed;
            if (!PublicKeySerialize(serializedKey, publicKey, serializedKeyFlags))
            {
                var errMsg = "Unmanaged EC library failed to serialize public key. ";
                throw new Exception(errMsg);
            }
        }
    }
}