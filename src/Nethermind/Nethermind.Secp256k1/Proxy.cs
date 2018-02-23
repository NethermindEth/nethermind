using System;
using System.Runtime.InteropServices;
using System.Security;

namespace Nethermind.Secp256k1
{
    // TODO: load correct libraries depending on win64/win32/posix
    // TODO: analyze security concerns for SuppressUnmanagedCodeSecurity
    // TODO: analyze memory access concerns when passing byte[] through P/Invoke
    // TODO: analyze differences between verify and recover + compare
    public static class Proxy
    {
/* constants from pycoin (https://github.com/richardkiss/pycoin)*/
        private const uint Secp256K1FlagsTypeMask = ((1 << 8) - 1);
        private const uint Secp256K1FlagsTypeContext = (1 << 0);

        private const uint Secp256K1FlagsTypeCompression = (1 << 1);

/* The higher bits contain the actual data. Do not use directly. */
        private const uint Secp256K1FlagsBitContextVerify = (1 << 8);
        private const uint Secp256K1FlagsBitContextSign = (1 << 9);
        private const uint Secp256K1FlagsBitCompression = (1 << 8);

/* Flags to pass to secp256k1_context_create. */
        private const uint Secp256K1ContextVerify = (Secp256K1FlagsTypeContext | Secp256K1FlagsBitContextVerify);
        private const uint Secp256K1ContextSign = (Secp256K1FlagsTypeContext | Secp256K1FlagsBitContextSign);
        private const uint Secp256K1ContextNone = (Secp256K1FlagsTypeContext);

        private const uint Secp256K1EcCompressed = (Secp256K1FlagsTypeCompression | Secp256K1FlagsBitCompression);
        private const uint Secp256K1EcUncompressed = (Secp256K1FlagsTypeCompression);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern /* secp256k1_context */ IntPtr secp256k1_context_create(uint flags);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern /* void */ IntPtr secp256k1_context_destroy(IntPtr context);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ec_seckey_verify( /* secp256k1_context */ IntPtr context, byte[] seckey);
        
        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ec_pubkey_create( /* secp256k1_context */ IntPtr context, byte[] pubkey, byte[] seckey);
        
        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ec_pubkey_serialize( /* secp256k1_context */ IntPtr context, byte[] serializedPublicKey, ref uint outputSize, byte[] publicKey, uint flags);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ecdsa_sign_recoverable( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] messageHash, byte[] privateKey, IntPtr nonceFunction, IntPtr nonceData);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ecdsa_recoverable_signature_serialize_compact( /* secp256k1_context */ IntPtr context, byte[] compactSignature, out int recoveryId, byte[] signature);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ecdsa_recoverable_signature_parse_compact( /* secp256k1_context */ IntPtr context, byte[] signature, byte[] compactSignature, int recoveryId);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ecdsa_recover( /* secp256k1_context */ IntPtr context, byte[] publicKey, byte[] signature, byte[] message);
        
        public static bool VerifyPrivateKey(byte[] privateKey)
        {
            IntPtr context = secp256k1_context_create(Secp256K1ContextNone);
            int result = secp256k1_ec_seckey_verify(context, privateKey);
            secp256k1_context_destroy(context);
            return result != 0;
        }

        public static byte[] GetPublicKey(byte[] privateKey, bool compressed)
        {
            byte[] publicKey = new byte[64];
            byte[] serializedPublicKey = new byte[compressed ? 33 : 65];
            IntPtr context = secp256k1_context_create(Secp256K1ContextSign | Secp256K1ContextVerify);
            secp256k1_ec_pubkey_create(context, publicKey, privateKey);
            uint outputSize = (uint)serializedPublicKey.Length;
            uint flags = compressed ? Secp256K1EcCompressed : Secp256K1EcUncompressed;
            secp256k1_ec_pubkey_serialize(context, serializedPublicKey, ref outputSize, publicKey, flags);
            secp256k1_context_destroy(context);
            return serializedPublicKey;
        }

        public static byte[] SignCompact(byte[] messageHash, byte[] privateKey, out int recoveryId)
        {
            byte[] recoverableSignature = new byte[65];
            byte[] compactSignature = new byte[64];
            IntPtr context = secp256k1_context_create(Secp256K1ContextSign | Secp256K1ContextVerify);
            secp256k1_ecdsa_sign_recoverable(context, recoverableSignature, messageHash, privateKey, IntPtr.Zero, IntPtr.Zero);
            secp256k1_ecdsa_recoverable_signature_serialize_compact(context, compactSignature, out recoveryId, recoverableSignature);
            secp256k1_context_destroy(context);
            return compactSignature;
        }
        
        public static byte[] RecoverKeyFromCompact(byte[] messageHash, byte[] compactSignature, int recoveryId, bool compressed)
        {
            byte[] publicKey = new byte[64];
            byte[] serializedPublicKey = new byte[compressed ? 33 : 65];
            uint outputSize = (uint)serializedPublicKey.Length;
            uint flags = compressed ? Secp256K1EcCompressed : Secp256K1EcUncompressed;
            
            byte[] recoverableSignature = new byte[65];
            IntPtr context = secp256k1_context_create(Secp256K1ContextSign | Secp256K1ContextVerify);
            secp256k1_ecdsa_recoverable_signature_parse_compact(context, recoverableSignature, compactSignature, recoveryId);
            secp256k1_ecdsa_recover(context, publicKey, recoverableSignature, messageHash);
            secp256k1_ec_pubkey_serialize(context, serializedPublicKey, ref outputSize, publicKey, flags);
            secp256k1_context_destroy(context);
            return serializedPublicKey;
        }
    }
}