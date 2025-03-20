// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Formats.Asn1;
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
    public static partial IntPtr EC_KEY_new_by_curve_name(int nid);

    [LibraryImport(LibraryName, EntryPoint = "EC_KEY_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EC_KEY_free(IntPtr key);

    [LibraryImport(LibraryName, EntryPoint = "EC_KEY_get0_group")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr EC_KEY_get0_group(IntPtr key);

    [LibraryImport(LibraryName, EntryPoint = "EC_POINT_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr EC_POINT_new(IntPtr group);

    [LibraryImport(LibraryName, EntryPoint = "EC_POINT_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EC_POINT_free(IntPtr point);

    [LibraryImport(LibraryName, EntryPoint = "BN_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr BN_new();

    [LibraryImport(LibraryName, EntryPoint = "BN_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void BN_free(IntPtr bn);

    [LibraryImport(LibraryName, EntryPoint = "BN_bin2bn")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial IntPtr BN_bin2bn(byte* bin, int len, IntPtr ret);

    [LibraryImport(LibraryName, EntryPoint = "EC_POINT_set_affine_coordinates_GFp")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EC_POINT_set_affine_coordinates_GFp(IntPtr group, IntPtr point, IntPtr x, IntPtr y, IntPtr ctx);

    [LibraryImport(LibraryName, EntryPoint = "EC_KEY_set_public_key")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EC_KEY_set_public_key(IntPtr key, IntPtr point);

    [LibraryImport(LibraryName, EntryPoint = "ECDSA_verify")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int ECDSA_verify(int type, byte* digest, nuint digest_len, byte* sig, nuint sig_len, IntPtr key);

    [LibraryImport(LibraryName, EntryPoint = "ECDSA_sign")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial int ECDSA_sign(int type, byte* digest, nuint digestLen, byte* signature, ref int sigLen, IntPtr ecKey);

    private const int NID_X9_62_prime256v1 = 415;

    public static unsafe IntPtr NewECKey(byte* x, byte* y)
    {
        IntPtr key = EC_KEY_new_by_curve_name(NID_X9_62_prime256v1);
        if (key == IntPtr.Zero)
            throw new Exception("EC_KEY_new_by_curve_name failed");

        IntPtr group = EC_KEY_get0_group(key);
        IntPtr pt = EC_POINT_new(group);
        if (pt == IntPtr.Zero)
        {
            EC_KEY_free(key);
            throw new Exception("EC_POINT_new failed");
        }

        IntPtr bx = BN_bin2bn(x, 32, IntPtr.Zero);
        IntPtr by = BN_bin2bn(y, 32, IntPtr.Zero);

        var ok = bx != IntPtr.Zero && by != IntPtr.Zero &&
            EC_POINT_set_affine_coordinates_GFp(group, pt, bx, by, IntPtr.Zero) != 0 &&
            EC_KEY_set_public_key(key, pt) != 0;

        if (bx != IntPtr.Zero) BN_free(bx);
        if (by != IntPtr.Zero) BN_free(by);
        EC_POINT_free(pt);

        if (!ok)
        {
            EC_KEY_free(key);
            //throw new Exception("EC_POINT_set_affine_coordinates_GFp failed");
            return IntPtr.Zero;
        }

        return key;
    }

    private static ReadOnlySpan<byte> RemoveLeadingZeroes(ReadOnlySpan<byte> span)
    {
        var i = 0;
        while (span[i] == 0 && i < span.Length - 1) i++;
        return span[i..];
    }

    private static byte[] EncodeSignature(ReadOnlySpan<byte> r, ReadOnlySpan<byte> s)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();

        writer.WriteIntegerUnsigned(RemoveLeadingZeroes(r));
        writer.WriteIntegerUnsigned(RemoveLeadingZeroes(s));

        writer.PopSequence();
        return writer.Encode();
    }

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> input, IReleaseSpec releaseSpec)
    {
        Console.WriteLine($"{GetType().Name}.{nameof(Run)}({Convert.ToHexString(input.Span)})");

        if (input.Length != 160)
            return (null, true);

        bool isValid;
        fixed (byte* ptr = input.Span)
        {
            var key = NewECKey(ptr + 96, ptr + 128);

            if (key == IntPtr.Zero)
                return (null, true);

            // var testSignature = new byte[200];
            // fixed (byte* sig = testSignature)
            // {
            //     var sigLen = testSignature.Length;
            //     if (ECDSA_sign(0, ptr, 32, sig, ref sigLen, key) == 0)
            //     {
            //         throw new Exception("ECDSA_sign failed");
            //     }
            //     var sigStr = Convert.ToHexString(testSignature[..sigLen]);
            // }

            var signature = EncodeSignature(input.Span[32..64], input.Span[64..96]);
            fixed (byte* sig = signature)
            {
                var res = ECDSA_verify(0, ptr, 32, sig, (UIntPtr)signature.Length, key);
                isValid = res != 0;
            }
        }

        Metrics.Secp256r1Precompile++;

        Console.WriteLine($"{GetType().Name}.{nameof(Run)}({Convert.ToHexString(input.Span)}) -> {isValid}");
        return (isValid ? ValidResult : null, true);
    }
}
