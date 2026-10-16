// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>ZisK's 256-bit arithmetic precompiles, entered directly rather than through a library call.</summary>
/// <remarks>
/// The ZisK guest lists <c>syscall_arith256_mod</c> and <c>syscall_arith256</c> in its JIT's <c>JitZkCsrPInvokes</c>,
/// so each call below is the single CSR write that runs the precompile, which changes no register. The library wrappers it replaces are calls,
/// around which every handler saved and reloaded the state it carries. Only code behind <c>ZiskFlag</c> reaches this.
/// Every operand is a 256-bit integer as four 64-bit limbs, least significant first; a result must not alias an
/// operand, and the modulus must not be zero.
/// </remarks>
internal static unsafe class ZiskArithmetic
{
    /// <summary>The precompile's operand block: it computes <c>(A * B + C) mod Module</c> into <c>D</c>.</summary>
    private struct Arith256ModParams
    {
        public ulong* A;
        public ulong* B;
        public ulong* C;
        public ulong* Module;
        public ulong* D;
    }

    /// <summary>Computes <c>(a * b) mod modulus</c> into <paramref name="result"/>, with the product taken over 512 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MulMod(ulong* a, ulong* b, ulong* modulus, ulong* result)
    {
        Debug.Assert((modulus[0] | modulus[1] | modulus[2] | modulus[3]) != 0);
        fixed (UInt256* zero = &Unsafe.AsRef(in UInt256.Zero))
        {
            Run(a, b, (ulong*)zero, modulus, result);
        }
    }

    /// <summary>Computes <c>(a + b) mod modulus</c> into <paramref name="result"/>, with the sum taken over 257 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddMod(ulong* a, ulong* b, ulong* modulus, ulong* result)
    {
        Debug.Assert((modulus[0] | modulus[1] | modulus[2] | modulus[3]) != 0);
        fixed (UInt256* one = &Unsafe.AsRef(in UInt256.One))
        {
            Run(a, (ulong*)one, b, modulus, result);
        }
    }

    /// <summary>Computes <c>a mod modulus</c> into <paramref name="result"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mod(ulong* a, ulong* modulus, ulong* result)
    {
        Debug.Assert((modulus[0] | modulus[1] | modulus[2] | modulus[3]) != 0);
        fixed (UInt256* one = &Unsafe.AsRef(in UInt256.One), zero = &Unsafe.AsRef(in UInt256.Zero))
        {
            Run(a, (ulong*)one, (ulong*)zero, modulus, result);
        }
    }

    /// <summary>The <c>arith256</c> operand block: it computes <c>A * B + C</c> into <c>Dh:Dl</c>.</summary>
    private struct Arith256Params
    {
        public ulong* A;
        public ulong* B;
        public ulong* C;
        public ulong* Dl;
        public ulong* Dh;
    }

    /// <summary>Computes the low 256 bits of <c>a * b</c> into <paramref name="low"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MultiplyLow(ulong* a, ulong* b, ulong* low)
    {
        UInt256 high;
        fixed (UInt256* zero = &Unsafe.AsRef(in UInt256.Zero))
        {
            Unsafe.SkipInit(out Arith256Params parameters);
            parameters.A = a;
            parameters.B = b;
            parameters.C = (ulong*)zero;
            parameters.Dl = low;
            parameters.Dh = (ulong*)&high;
            syscall_arith256(&parameters);
        }
    }

    [DllImport("__Internal", EntryPoint = "syscall_arith256", ExactSpelling = true), SuppressGCTransition]
    private static extern void syscall_arith256(Arith256Params* parameters);

    /// <summary>Runs the precompile on the operand block it builds; every field is written, so the block is not cleared first.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Run(ulong* a, ulong* b, ulong* c, ulong* modulus, ulong* result)
    {
        Unsafe.SkipInit(out Arith256ModParams parameters);
        parameters.A = a;
        parameters.B = b;
        parameters.C = c;
        parameters.Module = modulus;
        parameters.D = result;
        syscall_arith256_mod(&parameters);
    }

    [DllImport("__Internal", EntryPoint = "syscall_arith256_mod", ExactSpelling = true), SuppressGCTransition]
    private static extern void syscall_arith256_mod(Arith256ModParams* parameters);
}
