using System;
using System.Runtime.InteropServices;
using System.Security;

namespace Nethermind.Secp256k1
{
    // TODO: load correct libraries depending on win64/win32/posix
    // TODO: analyze security concerns for SuppressUnmanagedCodeSecurity
    public static class Proxy
    {
/* constants from pycoin (https://github.com/richardkiss/pycoin)*/
        private const int Secp256K1FlagsTypeMask = ((1 << 8) - 1);
        private const int Secp256K1FlagsTypeContext = (1 << 0);

        private const int Secp256K1FlagsTypeCompression = (1 << 1);

/* The higher bits contain the actual data. Do not use directly. */
        private const int Secp256K1FlagsBitContextVerify = (1 << 8);
        private const int Secp256K1FlagsBitContextSign = (1 << 9);
        private const int Secp256K1FlagsBitCompression = (1 << 8);

/* Flags to pass to secp256k1_context_create. */
        private const int Secp256K1ContextVerify = (Secp256K1FlagsTypeContext | Secp256K1FlagsBitContextVerify);
        private const int Secp256K1ContextSign = (Secp256K1FlagsTypeContext | Secp256K1FlagsBitContextSign);
        private const int Secp256K1ContextNone = (Secp256K1FlagsTypeContext);

        private const int Secp256K1EcCompressed = (Secp256K1FlagsTypeCompression | Secp256K1FlagsBitCompression);
        private const int Secp256K1EcUncompressed = (Secp256K1FlagsTypeCompression);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern /* secp256k1_context */ IntPtr secp256k1_context_create(uint flags);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern /* void */ IntPtr secp256k1_context_destroy(IntPtr context);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("secp256k1.dll")]
        private static extern int secp256k1_ec_seckey_verify( /* secp256k1_context */ IntPtr context, byte[] seckey);

        public static bool VerifyPrivateKey(byte[] privateKey)
        {
            IntPtr context = secp256k1_context_create(Secp256K1ContextNone);
            int result = secp256k1_ec_seckey_verify(context, privateKey);
            secp256k1_context_destroy(context);
            return result != 0;
        }

        public delegate bool VerifyDelegate(byte[] message, byte[] signature, byte[] publicKey, bool normalizeSignatureOnFailure);

        public delegate byte[] SignDelegate(byte[] message, byte[] privateKey);

        public delegate byte[] SignCompactDelegate(byte[] message, byte[] privateKey, out int recoveryId);

        public delegate byte[] RecoverKeyFromCompactDelegate(byte[] message, byte[] signature, int recoveryId, bool compressed);

        public delegate byte[] GetPublicKeyDelegate(byte[] privateKey, bool compressed);

        public delegate byte[] NormalizeSignatureDelegate(byte[] signature, out bool wasAlreadyNormalized);
    }
}