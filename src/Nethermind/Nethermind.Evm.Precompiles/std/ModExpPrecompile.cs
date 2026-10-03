// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.GmpBindings;

namespace Nethermind.Evm.Precompiles;

public unsafe partial class ModExpPrecompile
{
    /// <summary>
    /// Field primes whose MODEXP with an exponent of p - 2 is computed as a modular inverse instead.
    /// </summary>
    /// <remarks>
    /// For a prime p and a base b not divisible by p, b^(p-2) = b^-1 (mod p), and a modular inverse costs a fraction
    /// of the exponentiation ladder. Primality is never tested at run time: only these well-known primes qualify.
    /// The base reaches the inverse already reduced to [2, p - 1], since 0 and 1 are answered before it.
    /// </remarks>
    private static readonly byte[][] InversePrimes =
    [
        Convert.FromHexString("30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001"), // BN254 scalar field
        Convert.FromHexString("30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47"), // BN254 base field
        Convert.FromHexString("fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f"), // secp256k1 base field
        Convert.FromHexString("fffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141"), // secp256k1 group order
        Convert.FromHexString("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff"), // secp256r1 base field
        Convert.FromHexString("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551"), // secp256r1 group order
        Convert.FromHexString("73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001"), // BLS12-381 scalar field
        Convert.FromHexString("1a0111ea397fe69a4b1ba7b6434bacd764774b84f38512bf6730d2a0f6b0f6241eabfffeb153ffffb9feffffffffaaab"), // BLS12-381 base field
    ];

    /// <summary><see cref="InversePrimes"/> minus two, the exponent that makes the exponentiation an inverse.</summary>
    private static readonly byte[][] InverseExponents = Array.ConvertAll(InversePrimes, MinusTwo);

    private static bool s_inverseUnavailable;

    private static byte[] MinusTwo(byte[] prime)
    {
        byte[] value = (byte[])prime.Clone();
        int borrow = 2;
        for (int i = value.Length - 1; i >= 0 && borrow > 0; i--)
        {
            int digit = value[i] - borrow;
            borrow = digit < 0 ? 1 : 0;
            value[i] = (byte)digit;
        }

        return value;
    }

    private static bool IsKnownPrimeMinusTwo(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> exponent)
    {
        if (Volatile.Read(ref s_inverseUnavailable)) return false;
        ReadOnlySpan<byte> m = TrimLeadingZeros(modulus);
        // Every listed prime is 32 or 48 bytes, so the length rules out almost every other modulus first.
        if (m.Length is not (32 or 48)) return false;
        ReadOnlySpan<byte> e = TrimLeadingZeros(exponent);
        for (int i = 0; i < InversePrimes.Length; i++)
        {
            if (m.SequenceEqual(InversePrimes[i])) return e.SequenceEqual(InverseExponents[i]);
        }

        return false;
    }

    private static ReadOnlySpan<byte> TrimLeadingZeros(ReadOnlySpan<byte> value)
    {
        int first = value.IndexOfAnyExcept((byte)0);
        return first < 0 ? [] : value[first..];
    }

    /// <returns>Whether <paramref name="result"/> now holds the inverse of the reduced base.</returns>
    private static bool TryInvert(in mpz_t result, in mpz_t reducedBase, in mpz_t modulus)
    {
        try
        {
            // All three are the caller's stack locals, so their addresses hold without pinning.
            return mpz_invert(
                Unsafe.AsPointer(ref Unsafe.AsRef(in result)),
                Unsafe.AsPointer(ref Unsafe.AsRef(in reducedBase)),
                Unsafe.AsPointer(ref Unsafe.AsRef(in modulus))) != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // A libgmp without the export: every later call takes the exponentiation.
            Volatile.Write(ref s_inverseUnavailable, true);
            return false;
        }
    }

    /// <summary>GMP's <c>mpz_invert</c>, which the bindings do not expose; the same native library resolves it.</summary>
    [LibraryImport("gmp", EntryPoint = "__gmpz_invert")]
    private static partial int mpz_invert(void* rop, void* op1, void* op2);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.ModExpPrecompile++;

        if (!TryPrepareInput(
            inputData,
            releaseSpec,
            out ReadOnlySpan<byte> inputSpan,
            out uint baseLength,
            out uint expLength,
            out uint modulusLength,
            out Result<byte[]> errorOrEmpty))
        {
            return errorOrEmpty;
        }

        ulong expOffset = 96UL + baseLength;
        ulong modulusOffset = expOffset + expLength;
        uint expStart = expOffset > uint.MaxValue ? uint.MaxValue : (uint)expOffset;
        uint modulusStart = modulusOffset > uint.MaxValue ? uint.MaxValue : (uint)modulusOffset;

        using mpz_t modulusInt = mpz_t.Create();

        ReadOnlySpan<byte> modulusDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(modulusStart, modulusLength);

        if (modulusDataSpan.Length > 0)
        {
            fixed (byte* modulusData = &MemoryMarshal.GetReference(modulusDataSpan))
                Gmp.mpz_import(modulusInt, modulusLength, 1, 1, 1, nuint.Zero, modulusData);
        }

        if (Gmp.mpz_sgn(modulusInt) == 0)
            return new byte[modulusLength];

        using mpz_t baseInt = mpz_t.Create();
        using mpz_t expInt = mpz_t.Create();
        using mpz_t powmResult = mpz_t.Create();

        ReadOnlySpan<byte> baseDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(96U, baseLength);

        if (baseDataSpan.Length > 0)
        {
            fixed (byte* baseData = &MemoryMarshal.GetReference(baseDataSpan))
                Gmp.mpz_import(baseInt, baseLength, 1, 1, 1, nuint.Zero, baseData);
        }

        ReadOnlySpan<byte> expDataSpan = inputSpan.SliceWithZeroPaddingEmptyOnError(expStart, expLength);

        if (expDataSpan.Length > 0)
        {
            fixed (byte* expData = &MemoryMarshal.GetReference(expDataSpan))
                Gmp.mpz_import(expInt, expLength, 1, 1, 1, nuint.Zero, expData);
        }

        // Reduce the base before exponentiating. EIP-198 leaves the result unchanged, but a base that
        // falls to 0 or 1 makes the whole ladder constant, and the adversarial vectors in the execution-spec
        // benchmark suite are exactly that shape: the base is either equal to the modulus or one above it.
        Gmp.mpz_mod(baseInt, baseInt, modulusInt);

        if (Gmp.mpz_cmp_ui(baseInt, 1) <= 0 && Gmp.mpz_sgn(expInt) != 0)
        {
            byte[] trivial = new byte[modulusLength];
            // A reduced base of 1 raises to 1, which the modulus cannot be here since it would have reduced to 0.
            if (Gmp.mpz_sgn(baseInt) != 0)
                trivial[modulusLength - 1] = 1;
            return trivial;
        }

        // A prime modulus with an exponent of p - 2 is Fermat's inverse; verifiers use it for every field inversion.
        if (!IsKnownPrimeMinusTwo(modulusDataSpan, expDataSpan) || !TryInvert(powmResult, baseInt, modulusInt))
            Gmp.mpz_powm(powmResult, baseInt, expInt, modulusInt);

        nint powmResultLen = (nint)(Gmp.mpz_sizeinbase(powmResult, 2) + 7) / 8;
        nint offset = (int)modulusLength - powmResultLen;

        byte[] result = new byte[modulusLength];
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(result))
            Gmp.mpz_export(ptr + offset, out _, 1, 1, 1, nuint.Zero, powmResult);

        return result;
    }
}
