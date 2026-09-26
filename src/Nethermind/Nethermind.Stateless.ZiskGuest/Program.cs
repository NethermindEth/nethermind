// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.Guest;

partial class Program
{
    private static partial void WriteOutput(ReadOnlySpan<byte> output)
    {
        // For debugging purposes
        IO.PrintLine(Convert.ToHexStringLower(output));

        IO.WriteOutput(output);
    }

    /// <summary>Hands MULMOD to ZisK's <c>arith256_mod</c> accelerator before the first block runs.</summary>
    /// <remarks>
    /// A module initializer because <c>Main</c> is shared by every guest. Bound here rather than in
    /// Nethermind.Evm: the SP1 and OpenVM guests link that assembly too and have no such symbol.
    /// </remarks>
    [ModuleInitializer]
    internal static unsafe void UseArith256Mod() => ModularArithmetic.TryRegisterAccelerator(&Arith256Mod);

    /// <summary>Parameter block for <c>arith256_mod</c>, which computes <c>(a * b + c) mod module</c>.</summary>
    /// <remarks>The accelerator reads one pointer, so the five operand pointers travel as this block.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Arith256ModParameters
    {
        public ulong* A;
        public ulong* B;
        public ulong* C;
        public ulong* Module;
        public ulong* D;
    }

    /// <summary>Computes <c>(a * b) mod m</c> through the accelerator, for a non-zero <paramref name="m"/>.</summary>
    /// <remarks>
    /// The zero addend is a local rather than a <c>stackalloc</c>: zero-initialising one reaches ziskos'
    /// wrapped <c>memset</c>, whose DMA opcode the transpiler only accepts in its own call shape.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe void Arith256Mod(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
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

        syscall_arith256_mod(&parameters);
    }

    /// <summary>Points at a value's limbs. Callers hold every operand in a local, so none of them can move.</summary>
    private static unsafe ulong* Limbs(in UInt256 value) => (ulong*)Unsafe.AsPointer(ref Unsafe.AsRef(in value));

    /// <remarks>
    /// The <c>syscall_</c> entry point, not the <c>zkvm_</c> one: they are the same instruction, but the
    /// latter sits in an object file that also holds ziskos' DMA wrappers, and linking those in makes the
    /// transpiler reject the ROM - it reads the whole .text, and those wrappers are only legal in the call
    /// shape ziskos emits for them. This one has a section of its own, so nothing rides along. A
    /// <c>DllImport</c> rather than a <c>LibraryImport</c> because bflat compiles this file without source
    /// generators.
    /// </remarks>
    [DllImport("__Internal")]
    private static extern unsafe void syscall_arith256_mod(Arith256ModParameters* parameters);
}
