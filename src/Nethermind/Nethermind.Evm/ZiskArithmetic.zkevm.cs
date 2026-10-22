// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm;

/// <summary>The EVM's 256-bit arithmetic on ZisK's <c>arith256</c> and <c>arith256_mod</c> precompiles and its library division.</summary>
/// <remarks>
/// bflat lists both precompile P/Invokes in the JIT's <c>JitZkCsrPInvokes</c>, so each precompile call below is the single CSR
/// write that runs the precompile, which changes no register. The library wrappers they replace are calls, around
/// which every handler saved and reloaded the state it carries. Only code behind <c>ZiskFlag</c> reaches this.
/// Every operand is a 256-bit integer as four 64-bit limbs, least significant first; a result must not alias an
/// operand, and the modulus must not be zero.
/// </remarks>
internal static unsafe class ZiskArithmetic
{
    /// <summary>Computes <c>(a * b) mod modulus</c> into <paramref name="result"/>, with the product taken over 512 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MulMod(ulong* a, ulong* b, ulong* modulus, ulong* result)
    {
        Debug.Assert((modulus[0] | modulus[1] | modulus[2] | modulus[3]) != 0);
        fixed (UInt256* zero = &Unsafe.AsRef(in UInt256.Zero))
            Accelerators.Arith256Mod(a, b, (ulong*)zero, modulus, result);
    }

    /// <summary>Computes <c>(a + b) mod modulus</c> into <paramref name="result"/>, with the sum taken over 257 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddMod(ulong* a, ulong* b, ulong* modulus, ulong* result)
    {
        Debug.Assert((modulus[0] | modulus[1] | modulus[2] | modulus[3]) != 0);
        fixed (UInt256* one = &Unsafe.AsRef(in UInt256.One))
            Accelerators.Arith256Mod(a, (ulong*)one, b, modulus, result);
    }

    /// <summary>Computes <c>a mod modulus</c> into <paramref name="result"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mod(ulong* a, ulong* modulus, ulong* result)
    {
        Debug.Assert((modulus[0] | modulus[1] | modulus[2] | modulus[3]) != 0);
        fixed (UInt256* one = &Unsafe.AsRef(in UInt256.One), zero = &Unsafe.AsRef(in UInt256.Zero))
            Accelerators.Arith256Mod(a, (ulong*)one, (ulong*)zero, modulus, result);
    }

    /// <summary>Computes the quotient <c>a / b</c> into <paramref name="quotient"/>, which may alias an operand; a zero divisor gives zero.</summary>
    /// <remarks>The ZisK library routine behind the little-endian U256 ABI, reached through its call thunk in the runtime.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Divide(ulong* a, ulong* b, ulong* quotient) => zkvm_u256_le_div(a, b, quotient);

    /// <summary>Computes the low 256 bits of <c>a * b</c> into <paramref name="low"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyLow(ulong* a, ulong* b, ulong* low)
    {
        UInt256 high;
        fixed (UInt256* zero = &Unsafe.AsRef(in UInt256.Zero))
            Accelerators.Arith256(a, b, (ulong*)zero, low, (ulong*)&high);
    }

    [DllImport("__Internal", ExactSpelling = true), SuppressGCTransition]
    private static extern int zkvm_u256_le_div(ulong* a, ulong* b, ulong* quotient);
}
