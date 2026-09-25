// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable NETH003 // Build variant: only one of ModularArithmetic.std.cs / ModularArithmetic.zkevm.cs is compiled per build

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>Modular arithmetic behind the three-parameter EVM instructions.</summary>
/// <remarks>
/// MULMOD is the one EVM operation a 256-bit software path cannot do cheaply: a full 512-bit product
/// followed by a 512-by-256 division. ZisK carries it as a single accelerator, so the guest hands the
/// operands to that instead - the same trade the other precompiles already make for keccak and secp256k1.
/// </remarks>
internal static unsafe partial class ModularArithmetic
{
    /// <summary>Parameter block for <c>arith256_mod</c>, which computes <c>(a * b + c) mod module</c>.</summary>
    /// <remarks>The accelerator reads one pointer, so the five operand pointers travel as this block.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct Arith256ModParameters
    {
        public ulong* A;
        public ulong* B;
        public ulong* C;
        public ulong* Module;
        public ulong* D;
    }

    /// <summary>Whether the accelerator answered the startup check; false on a host that has no ZisK.</summary>
    /// <remarks>
    /// The binding resolves at link time in the guest, so this is always true there. It is false under the
    /// zkEVM unit tests, which run this same build variant on an ordinary machine where the symbol is absent.
    /// </remarks>
    private static readonly bool _accelerated = ProbeAccelerator();

    /// <inheritdoc cref="UInt256.MultiplyMod(in UInt256, in UInt256, in UInt256, out UInt256)"/>
    public static void MultiplyMod(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        // The EVM defines a zero modulus as a zero result, where the accelerator has no answer and the
        // software path throws. `Math3ParamCore` never asks - it leaves a zero modulus on the stack
        // untouched - so this only keeps the helper total for callers that do.
        if (m.IsZero)
        {
            result = default;
            return;
        }

        if (!_accelerated)
        {
            UInt256.MultiplyMod(in a, in b, in m, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        Arith256Mod(in a, in b, in m, out result);
    }

    /// <summary>Computes <c>(a * b) mod m</c> through the accelerator, for a non-zero <paramref name="m"/>.</summary>
    /// <remarks>
    /// The zero addend is a local rather than a <c>stackalloc</c>: zero-initialising one reaches ziskos'
    /// wrapped <c>memset</c>, whose DMA opcode the transpiler only accepts in its own call shape.
    /// </remarks>
    [SkipLocalsInit]
    private static void Arith256Mod(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        UInt256 addend = default;

        Unsafe.SkipInit(out result);
        Arith256ModParameters parameters = new()
        {
            A = Limbs(in a),
            B = Limbs(in b),
            C = Limbs(in addend),
            Module = Limbs(in m),
            D = (ulong*)Unsafe.AsPointer(ref result)
        };

        zkvm_arith256_mod((nint)(&parameters));
    }

    /// <summary>Points at a value's limbs. Callers hold every operand in a local, so none of them can move.</summary>
    private static ulong* Limbs(in UInt256 value) => (ulong*)Unsafe.AsPointer(ref Unsafe.AsRef(in value));

    /// <remarks>
    /// Checks the accelerator against a case whose answer is fixed rather than only checking that the call
    /// returns: a binding that resolved to the wrong routine would otherwise go unnoticed until a block
    /// produced a wrong root.
    /// </remarks>
    private static bool ProbeAccelerator()
    {
        try
        {
            UInt256 a = 7;
            UInt256 b = 11;
            UInt256 m = 13;
            Arith256Mod(in a, in b, in m, out UInt256 probe);

            // 7 * 11 = 77, and 77 mod 13 is 12.
            return probe == (UInt256)12;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <remarks>
    /// The <c>syscall_</c> entry point, not the <c>zkvm_</c> one: they are the same instruction, but the
    /// latter sits in an object file that also holds ziskos' DMA wrappers, and linking those in makes the
    /// transpiler reject the ROM - it reads the whole .text, and those wrappers are only legal in the call
    /// shape ziskos emits for them. This one has a section of its own, so nothing rides along.
    /// </remarks>
    [LibraryImport("__Internal", EntryPoint = "syscall_arith256_mod")]
    private static partial void zkvm_arith256_mod(nint parameters);
}
