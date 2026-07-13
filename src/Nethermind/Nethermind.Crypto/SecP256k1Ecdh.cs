// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

// The public SecP256k1.Ecdh API returns the default ECDH secret, while discv5 needs the compressed shared EC point.
internal static unsafe class SecP256k1Ecdh
{
    private const uint ContextNone = 1;
    private const int ParsedPublicKeyLength = 64;
    private const int CoordinateLength = 32;

    private static readonly EcdhHashFunction CompressedPointHashFunction = WriteCompressedPoint;
    private static readonly SecP256k1ContextHandle Context = new();

    [SkipLocalsInit]
    internal static byte[] GetCompressedSharedPoint(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey)
    {
        if (Context.IsInvalid)
        {
            throw new InvalidOperationException("Failed to create secp256k1 context.");
        }

        Span<byte> parsedPublicKey = stackalloc byte[ParsedPublicKeyLength];
        Span<byte> output = stackalloc byte[CompressedPublicKey.LengthInBytes];

        fixed (byte* publicKeyPtr = publicKey)
        fixed (byte* privateKeyPtr = privateKey)
        fixed (byte* parsedPublicKeyPtr = parsedPublicKey)
        fixed (byte* outputPtr = output)
        {
            if (secp256k1_ec_pubkey_parse(Context, parsedPublicKeyPtr, publicKeyPtr, (nuint)publicKey.Length) != 1)
            {
                throw new ArgumentException("Invalid secp256k1 public key.", nameof(publicKey));
            }

            if (secp256k1_ecdh(Context, outputPtr, parsedPublicKeyPtr, privateKeyPtr, CompressedPointHashFunction, IntPtr.Zero) != 1)
            {
                throw new ArgumentException("Invalid secp256k1 private key.", nameof(privateKey));
            }
        }

        return output.ToArray();
    }

    // The unmanaged callback receives libsecp256k1-owned 32-byte coordinates for the shared point and writes
    // the compressed EC point form required by the devp2p discv5 key agreement.
    private static int WriteCompressedPoint(byte* output, byte* x32, byte* y32, IntPtr data)
    {
        output[0] = (byte)(2 | (y32[CoordinateLength - 1] & 1));
        new ReadOnlySpan<byte>(x32, CoordinateLength).CopyTo(new Span<byte>(output + 1, CoordinateLength));
        return 1;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EcdhHashFunction(byte* output, byte* x32, byte* y32, IntPtr data);

    [DllImport("secp256k1", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secp256k1_context_create(uint flags);

    [DllImport("secp256k1", CallingConvention = CallingConvention.Cdecl)]
    private static extern void secp256k1_context_destroy(IntPtr context);

    [DllImport("secp256k1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int secp256k1_ec_pubkey_parse(
        SecP256k1ContextHandle context,
        byte* publicKey,
        byte* input,
        nuint inputLength);

    [DllImport("secp256k1", CallingConvention = CallingConvention.Cdecl)]
    private static extern int secp256k1_ecdh(
        SecP256k1ContextHandle context,
        byte* output,
        byte* publicKey,
        byte* privateKey,
        EcdhHashFunction hashFunction,
        IntPtr data);

    private sealed class SecP256k1ContextHandle : SafeHandle
    {
        public SecP256k1ContextHandle()
            : base(IntPtr.Zero, ownsHandle: true)
            => handle = secp256k1_context_create(ContextNone);

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            secp256k1_context_destroy(handle);
            return true;
        }
    }
}
