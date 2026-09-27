// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Evm;
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

    /// <remarks>
    /// libziskos exports 256-bit modular arithmetic and division on top of its arith256 precompiles,
    /// outside the shared zkvm_* interface, so SP1 and OpenVM have no counterpart and only this guest
    /// can import them. Each is one precompile call (division verifies a hinted quotient) against
    /// hundreds to about 1,800 steps for the software UInt256 routines. The hinted division stays sound:
    /// div_rem256_c checks that q·b + r equals a with a zero high word and that r is below b, so a prover
    /// cannot substitute another pair.
    /// </remarks>
    static partial void InstallAccelerators()
    {
        unsafe
        {
            Arith256Accelerators.Install(&AddMod256, &MulMod256, &ReduceMod256, &DivRem256);
        }
    }

    // The routines read their operands, run one precompile and write the result; they neither block
    // nor call back into managed code, so the GC transition is safe to skip. All pointers are to four
    // little-endian 64-bit limbs, UInt256's layout.
    [DllImport("__Internal", EntryPoint = "add_mod256_c", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void AddMod256(ulong* a, ulong* b, ulong* modulus, ulong* result);

    [DllImport("__Internal", EntryPoint = "mul_mod256_c", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void MulMod256(ulong* a, ulong* b, ulong* modulus, ulong* result);

    [DllImport("__Internal", EntryPoint = "reduce_mod256_c", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void ReduceMod256(ulong* a, ulong* modulus, ulong* result);

    [DllImport("__Internal", EntryPoint = "div_rem256_c", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void DivRem256(ulong* a, ulong* b, ulong* quotient, ulong* remainder);
}
