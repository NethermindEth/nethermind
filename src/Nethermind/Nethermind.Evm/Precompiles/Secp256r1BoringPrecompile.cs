// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Secp256r1BoringPrecompile : IPrecompile<Secp256r1BoringPrecompile>
{
    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1BoringPrecompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    private const string LibraryName = "Binaries/boringssl/crypto";

    [LibraryImport(LibraryName, EntryPoint = "EC_KEY_new_by_curve_name")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint EC_KEY_new_by_curve_name(int nid);

    [LibraryImport(LibraryName, EntryPoint = "EC_KEY_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EC_KEY_free(nint key);

    [LibraryImport(LibraryName, EntryPoint = "EC_KEY_get0_group")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint EC_KEY_get0_group(nint key);

    [LibraryImport(LibraryName, EntryPoint = "EC_POINT_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint EC_POINT_new(nint group);

    [LibraryImport(LibraryName, EntryPoint = "EC_POINT_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EC_POINT_free(nint point);

    [LibraryImport(LibraryName, EntryPoint = "BN_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void BN_free(nint bn);

    [LibraryImport(LibraryName, EntryPoint = "BN_bin2bn")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial nint BN_bin2bn(byte* bin, int len, nint ret);

    [LibraryImport(LibraryName, EntryPoint = "EC_POINT_set_affine_coordinates_GFp")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EC_POINT_set_affine_coordinates_GFp(nint group, nint point, nint x, nint y, nint ctx);

    [LibraryImport(LibraryName, EntryPoint = "EC_KEY_set_public_key")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EC_KEY_set_public_key(nint key, nint point);

    [LibraryImport(LibraryName, EntryPoint = "ECDSA_verify")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int ECDSA_verify(int type, byte* digest, nint digest_len, byte* sig, nint sig_len, nint key);

    private const int NID_X9_62_prime256v1 = 415;

    public static unsafe nint NewECKey(byte* x, byte* y)
    {
        var key = EC_KEY_new_by_curve_name(NID_X9_62_prime256v1);
        if (key == 0)
            throw new Exception("EC_KEY_new_by_curve_name failed");

        var group = EC_KEY_get0_group(key);
        var pt = EC_POINT_new(group);
        if (pt == 0)
        {
            EC_KEY_free(key);
            throw new Exception("EC_POINT_new failed");
        }

        var bx = BN_bin2bn(x, 32, 0);
        var by = BN_bin2bn(y, 32, 0);

        var ok = bx != 0 && by != 0 &&
            EC_POINT_set_affine_coordinates_GFp(group, pt, bx, by, 0) != 0 &&
            EC_KEY_set_public_key(key, pt) != 0;

        if (bx != 0) BN_free(bx);
        if (by != 0) BN_free(by);
        EC_POINT_free(pt);

        if (!ok)
        {
            EC_KEY_free(key);
            //throw new Exception("EC_POINT_set_affine_coordinates_GFp failed");
            return 0;
        }

        return key;
    }

    // Encodes signature (r,s) in DER format
    // AsnWriter allocates too much
    private static ReadOnlySpan<byte> EncodeSignature(ReadOnlySpan<byte> r, ReadOnlySpan<byte> s, Span<byte> buffer)
    {
        buffer[0] = 0x30; // SEQUENCE OF

        var index = 2;
        index += EncodeUnsignedInteger(r, buffer[index..]);
        index += EncodeUnsignedInteger(s, buffer[index..]);

        buffer[1] = (byte)(index - 2);  // SEQUENCE OF length

        return buffer[..index];
    }

    private static int EncodeUnsignedInteger(ReadOnlySpan<byte> value, Span<byte> buffer)
    {
        // Skip zeroes
        var valIndex = 0;
        while (value[valIndex] == 0 && valIndex < value.Length - 1) valIndex++;
        value = value[valIndex..];

        buffer[0] = 0x02; // INTEGER;
        buffer[1] = (byte)value.Length; // INTEGER length
        var buffIndex = 2;

        // Add leading zero if number is negative
        if ((value[0] & 0x80) != 0)
        {
            buffer[1]++;
            buffer[buffIndex++] = 0;
        }

        value.CopyTo(buffer[buffIndex..]);
        return buffIndex + value.Length;
    }

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> input, IReleaseSpec releaseSpec)
    {
        //Console.WriteLine($"{GetType().Name}.{nameof(Run)}({Convert.ToHexString(input.Span)})");

        if (input.Length != 160)
            return (null, true);

        bool isValid;
        nint key = 0;

        try
        {
            Span<byte> buffer = stackalloc byte[2 + 2 * (2 + 32 + 1)]; // Max possible size when DER-encoded
            var signature = EncodeSignature(input.Span[32..64], input.Span[64..96], buffer);

            fixed (byte* ptr = input.Span)
            fixed (byte* sig = signature)
            {
                key = NewECKey(ptr + 96, ptr + 128);
                if (key == 0) return (null, true);

                isValid = ECDSA_verify(0, ptr, 32, sig, signature.Length, key) != 0;
            }
        }
        finally
        {
            if (key != 0) EC_KEY_free(key);
        }

        Metrics.Secp256r1Precompile++;

        return (isValid ? ValidResult : null, true);
    }
}
