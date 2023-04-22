// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

/* ECDH bindings were based on ECDH bindings from Secp256k1.Net (MIT license) */

using System;
using System.Runtime.InteropServices;
using System.Security;
using Nethermind.Native;


namespace Nethermind.Secp256k1
{
    public static partial class Proxy
    {
        private const string Secp256k1 = "secp256k1";

        static Proxy()
        {
            NativeLibrary.SetDllImportResolver(typeof(Proxy).Assembly, NativeLib.ImportResolver);
            Context = CreateContext();
        }

        /*****************************************************************************************/
        /*****************************************************************************************/
        /*****************************************************************************************/

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        public static partial IntPtr secp256k1_context_create(uint flags);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        public static partial IntPtr secp256k1_context_destroy(IntPtr context);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool secp256k1_ec_seckey_verify( /* secp256k1_context */ IntPtr context, byte[] seckey);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static unsafe partial bool secp256k1_ec_pubkey_create( /* secp256k1_context */ IntPtr context, void* pubkey, byte[] seckey);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static unsafe partial bool secp256k1_ec_pubkey_serialize( /* secp256k1_context */ IntPtr context, void* serializedPublicKey, ref uint outputSize, void* publicKey, uint flags);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool secp256k1_ecdsa_sign_recoverable( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] messageHash, byte[] privateKey, IntPtr nonceFunction, IntPtr nonceData);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool secp256k1_ecdsa_recoverable_signature_serialize_compact( /* secp256k1_context */ IntPtr context, byte[] compactSignature, out int recoveryId, byte[] signature);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static unsafe partial bool secp256k1_ecdsa_recoverable_signature_parse_compact( /* secp256k1_context */ IntPtr context, void* signature, void* compactSignature, int recoveryId);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static unsafe partial bool secp256k1_ecdsa_recover( /* secp256k1_context */ IntPtr context, void* publicKey, void* signature, byte[] message);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool secp256k1_ecdh( /* secp256k1_context */ IntPtr context, byte[] output, byte[] publicKey, byte[] privateKey, IntPtr hashFunctionPointer, IntPtr data);

        [SuppressUnmanagedCodeSecurity]
        [LibraryImport(Secp256k1)]
        public static unsafe partial int secp256k1_ec_pubkey_parse(IntPtr ctx, void* pubkey, void* input, uint inputlen);


        /*****************************************************************************************/
        /*****************************************************************************************/
        /*****************************************************************************************/

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

        private static readonly IntPtr Context;

        private static IntPtr CreateContext()
        {
            return secp256k1_context_create(Secp256K1ContextSign | Secp256K1ContextVerify);
        }

        public static bool VerifyPrivateKey(byte[] privateKey)
        {
            return secp256k1_ec_seckey_verify(Context, privateKey);
        }

        public static unsafe byte[] GetPublicKey(byte[] privateKey, bool compressed)
        {
            Span<byte> publicKey = stackalloc byte[64];
            Span<byte> serializedPublicKey = stackalloc byte[compressed ? 33 : 65];

            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(serializedPublicKey), pubKeyPtr = &MemoryMarshal.GetReference(publicKey))
            {
                bool keyDerivationFailed =
                    !secp256k1_ec_pubkey_create(Context, pubKeyPtr, privateKey);
                if (keyDerivationFailed)
                {
                    return null;
                }

                uint outputSize = (uint)serializedPublicKey.Length;
                uint flags = compressed ? Secp256K1EcCompressed : Secp256K1EcUncompressed;

                bool serializationFailed =
                    !secp256k1_ec_pubkey_serialize(Context, serializedPtr, ref outputSize, pubKeyPtr, flags);
                if (serializationFailed)
                {
                    return null;
                }
            }

            return serializedPublicKey.ToArray();
        }

        public static byte[] SignCompact(byte[] messageHash, byte[] privateKey, out int recoveryId)
        {
            byte[] recoverableSignature = new byte[65];
            recoveryId = 0;

            if (!secp256k1_ecdsa_sign_recoverable(
                Context, recoverableSignature, messageHash, privateKey, IntPtr.Zero, IntPtr.Zero))
            {
                return null;
            }

            byte[] compactSignature = new byte[64];
            if (!secp256k1_ecdsa_recoverable_signature_serialize_compact(
                Context, compactSignature, out recoveryId, recoverableSignature))
            {
                return null;
            }

            return compactSignature;
        }

        // public static unsafe bool RecoverKeyFromCompact(Span<byte> output, byte[] messageHash, Span<byte> recoverableSignature, bool compressed)
        // {
        //     Span<byte> publicKey = stackalloc byte[64];
        //     int expectedLength = compressed ? 33 : 65;
        //     if (output.Length != expectedLength)
        //     {
        //         throw new ArgumentException($"{nameof(output)} length should be {expectedLength}");
        //     }
        //
        //     fixed (byte*
        //         pubKeyPtr = &MemoryMarshal.GetReference(publicKey),
        //         recoverableSignaturePtr = &MemoryMarshal.GetReference(recoverableSignature),
        //         serializedPublicKeyPtr = &MemoryMarshal.GetReference(output))
        //     {
        //         if (!secp256k1_ecdsa_recover(Context, pubKeyPtr, recoverableSignaturePtr, messageHash))
        //         {
        //             return false;
        //         }
        //         
        //         uint flags = compressed ? Secp256K1EcCompressed : Secp256K1EcUncompressed;
        //         
        //         uint outputSize = (uint) output.Length;
        //         if (!secp256k1_ec_pubkey_serialize(
        //             Context, serializedPublicKeyPtr, ref outputSize, pubKeyPtr, flags))
        //         {
        //             return false;
        //         }
        //
        //         return true;
        //     }
        // }

        public static unsafe bool RecoverKeyFromCompact(Span<byte> output, byte[] messageHash, Span<byte> compactSignature, int recoveryId, bool compressed)
        {
            Span<byte> recoverableSignature = stackalloc byte[65];
            Span<byte> publicKey = stackalloc byte[64];
            int expectedLength = compressed ? 33 : 65;
            if (output.Length != expectedLength)
            {
                throw new ArgumentException($"{nameof(output)} length should be {expectedLength}");
            }

            fixed (byte*
                compactSigPtr = &MemoryMarshal.GetReference(compactSignature),
                pubKeyPtr = &MemoryMarshal.GetReference(publicKey),
                recoverableSignaturePtr = &MemoryMarshal.GetReference(recoverableSignature),
                serializedPublicKeyPtr = &MemoryMarshal.GetReference(output))
            {
                if (!secp256k1_ecdsa_recoverable_signature_parse_compact(
                    Context, recoverableSignaturePtr, compactSigPtr, recoveryId))
                {
                    return false;
                }

                if (!secp256k1_ecdsa_recover(Context, pubKeyPtr, recoverableSignaturePtr, messageHash))
                {
                    return false;
                }

                uint flags = compressed ? Secp256K1EcCompressed : Secp256K1EcUncompressed;

                uint outputSize = (uint)output.Length;
                if (!secp256k1_ec_pubkey_serialize(
                    Context, serializedPublicKeyPtr, ref outputSize, pubKeyPtr, flags))
                {
                    return false;
                }

                return true;
            }
        }

        unsafe delegate int secp256k1_ecdh_hash_function(void* output, void* x, void* y, IntPtr data);

        public static unsafe bool Ecdh(byte[] agreement, byte[] publicKey, byte[] privateKey)
        {
            int outputLength = agreement.Length;

            // TODO: should probably do that only once
            secp256k1_ecdh_hash_function hashFunctionPtr = (void* output, void* x, void* y, IntPtr d) =>
            {
                Span<byte> outputSpan = new(output, outputLength);
                Span<byte> xSpan = new(x, 32);
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
                    return secp256k1_ecdh(Context, agreement, publicKey, privateKey, fp, IntPtr.Zero);
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

        public static byte[] Decompress(Span<byte> compressed)
        {
            Span<byte> serializedKey = stackalloc byte[65];
            byte[] publicKey = new byte[64];
            PublicKeyParse(publicKey, compressed);

            if (!PublicKeySerialize(serializedKey, publicKey))
            {
                throw new Exception("Failed toi serialize");
            }

            return serializedKey.ToArray();
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
        private static unsafe bool PublicKeyParse(Span<byte> publicKeyOutput, Span<byte> serializedPublicKey)
        {
            int inputLen = serializedPublicKey.Length;
            if (inputLen != 33 && inputLen != 65)
            {
                throw new ArgumentException($"{nameof(serializedPublicKey)} must be 33 or 65 bytes");
            }

            if (publicKeyOutput.Length < 64)
            {
                throw new ArgumentException($"{nameof(publicKeyOutput)} must be {64} bytes");
            }

            fixed (byte* pubKeyPtr = &MemoryMarshal.GetReference(publicKeyOutput), serializedPtr = &MemoryMarshal.GetReference(serializedPublicKey))
            {
                return secp256k1_ec_pubkey_parse(
                    Context, pubKeyPtr, serializedPtr, (uint)inputLen) == 1;
            }
        }

        /// <summary>
        /// Serialize a pubkey object into a serialized byte sequence.
        /// </summary>
        /// <param name="serializedPublicKeyOutput">65-byte (if compressed==0) or 33-byte (if compressed==1) output to place the serialized key in.</param>
        /// <param name="publicKey">The secp256k1_pubkey initialized public key.</param>
        /// <param name="flags">SECP256K1_EC_COMPRESSED if serialization should be in compressed format, otherwise SECP256K1_EC_UNCOMPRESSED.</param>
        private static unsafe bool PublicKeySerialize(Span<byte> serializedPublicKeyOutput, Span<byte> publicKey, uint flags = Secp256K1EcUncompressed)
        {
            bool compressed = (flags & Secp256K1EcCompressed) == Secp256K1EcCompressed;
            int serializedPubKeyLength = compressed ? 33 : 65;
            if (serializedPublicKeyOutput.Length < serializedPubKeyLength)
            {
                string compressedStr = compressed ? "compressed" : "uncompressed";
                throw new ArgumentException($"{nameof(serializedPublicKeyOutput)} ({compressedStr}) must be {serializedPubKeyLength} bytes");
            }

            int expectedInputLength = flags == Secp256K1EcCompressed ? 33 : 64;
            if (publicKey.Length != expectedInputLength)
            {
                throw new ArgumentException($"{nameof(publicKey)} must be {expectedInputLength} bytes");
            }

            uint newLength = (uint)serializedPubKeyLength;

            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(serializedPublicKeyOutput), pubKeyPtr = &MemoryMarshal.GetReference(publicKey))
            {
                bool success = secp256k1_ec_pubkey_serialize(
                    Context, serializedPtr, ref newLength, pubKeyPtr, flags);

                return success && newLength == serializedPubKeyLength;
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
                const string errMsg = "Unmanaged EC library failed to deserialize public key. ";
                throw new Exception(errMsg);
            }

            // Serialize the public key
            if (!PublicKeySerialize(serializedKey, publicKey, Secp256K1EcUncompressed))
            {
                const string errMsg = "Unmanaged EC library failed to serialize public key. ";
                throw new Exception(errMsg);
            }
        }
    }
}
