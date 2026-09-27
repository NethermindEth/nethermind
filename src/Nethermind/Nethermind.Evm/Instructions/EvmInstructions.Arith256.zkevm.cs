// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

using Int256;

public static unsafe partial class EvmInstructions
{
    // Each routine falls back to the software UInt256 path when no accelerator is installed, which is
    // every guest except ZisK and every zkEVM test on the host. The operands are pinned rather than
    // copied: DIV and MOD read them in place from stack slots, and a pinned local is free on NativeAOT.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void AddMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        delegate*<ulong*, ulong*, ulong*, ulong*, void> addMod = Arith256Accelerators.AddModRoutine;
        if (addMod is null)
        {
            UInt256.AddMod(in a, in b, in m, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        fixed (UInt256* pa = &a, pb = &b, pm = &m, pr = &result)
            addMod((ulong*)pa, (ulong*)pb, (ulong*)pm, (ulong*)pr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void MulMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        delegate*<ulong*, ulong*, ulong*, ulong*, void> mulMod = Arith256Accelerators.MulModRoutine;
        if (mulMod is null)
        {
            UInt256.MultiplyMod(in a, in b, in m, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        fixed (UInt256* pa = &a, pb = &b, pm = &m, pr = &result)
            mulMod((ulong*)pa, (ulong*)pb, (ulong*)pm, (ulong*)pr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Divide256(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        delegate*<ulong*, ulong*, ulong*, ulong*, void> divRem = Arith256Accelerators.DivRemRoutine;
        if (divRem is null)
        {
            UInt256.Divide(in a, in b, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        Unsafe.SkipInit(out UInt256 remainder);
        fixed (UInt256* pa = &a, pb = &b, pq = &result)
            divRem((ulong*)pa, (ulong*)pb, (ulong*)pq, (ulong*)&remainder);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Mod256(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        delegate*<ulong*, ulong*, ulong*, void> reduce = Arith256Accelerators.ReduceModRoutine;
        if (reduce is null)
        {
            UInt256.Mod(in a, in b, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        fixed (UInt256* pa = &a, pb = &b, pr = &result)
            reduce((ulong*)pa, (ulong*)pb, (ulong*)pr);
    }
}
