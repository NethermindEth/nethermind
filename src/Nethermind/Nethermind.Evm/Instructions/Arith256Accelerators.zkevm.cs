// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

/// <summary>A zkVM's native 256-bit division and modular arithmetic, as installed by its guest.</summary>
/// <remarks>
/// The three guests compile one managed closure, and only ZisK's runtime exports these routines, so the
/// EVM cannot import them itself: an import SP1 or OpenVM cannot resolve would fail their link. A guest
/// whose zkVM has them installs them before execution starts (the ZisK guest does, in its Program.cs);
/// the others install nothing and the opcodes keep the software <c>UInt256</c> path.
/// <para>
/// Every routine takes pointers to four little-endian 64-bit limbs, the <c>UInt256</c> layout, and is
/// only called with a non-zero divisor or modulus. Results are written only after the inputs are read,
/// so an output may not alias an input; the callers in <see cref="EvmInstructions"/> never alias them.
/// </para>
/// </remarks>
public static unsafe class Arith256Accelerators
{
    internal static delegate*<ulong*, ulong*, ulong*, ulong*, void> AddModRoutine;
    internal static delegate*<ulong*, ulong*, ulong*, ulong*, void> MulModRoutine;
    internal static delegate*<ulong*, ulong*, ulong*, void> ReduceModRoutine;
    internal static delegate*<ulong*, ulong*, ulong*, ulong*, void> DivRemRoutine;

    /// <summary>Routes ADDMOD, MULMOD, MOD and DIV to the given routines for the rest of the run.</summary>
    /// <param name="addMod"><c>result = (a + b) mod m</c>, with the sum taken over 257 bits.</param>
    /// <param name="mulMod"><c>result = (a * b) mod m</c>, with the product taken over 512 bits.</param>
    /// <param name="reduceMod"><c>result = a mod m</c>.</param>
    /// <param name="divRem"><c>quotient = a / b</c> and <c>remainder = a mod b</c>.</param>
    public static void Install(
        delegate*<ulong*, ulong*, ulong*, ulong*, void> addMod,
        delegate*<ulong*, ulong*, ulong*, ulong*, void> mulMod,
        delegate*<ulong*, ulong*, ulong*, void> reduceMod,
        delegate*<ulong*, ulong*, ulong*, ulong*, void> divRem)
    {
        AddModRoutine = addMod;
        MulModRoutine = mulMod;
        ReduceModRoutine = reduceMod;
        DivRemRoutine = divRem;
    }
}
