// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm;

using Int256;

public static unsafe partial class EvmInstructions
{
    // Each routine keeps the software UInt256 path unless the ZisK guest switched ZiskArith256Flag on, so
    // every other guest and every zkEVM test on the host runs it. The operands are pinned rather than
    // copied: DIV and MOD read them in place from stack slots, and a pinned local is free on NativeAOT.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void AddMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        if (!ZiskArith256Flag.IsActive)
        {
            UInt256.AddMod(in a, in b, in m, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        fixed (UInt256* pa = &a, pb = &b, pm = &m, pr = &result)
            Accelerators.AddMod256((ulong*)pa, (ulong*)pb, (ulong*)pm, (ulong*)pr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void MulMod256(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        if (!ZiskArith256Flag.IsActive)
        {
            UInt256.MultiplyMod(in a, in b, in m, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        fixed (UInt256* pa = &a, pb = &b, pm = &m, pr = &result)
            Accelerators.MulMod256((ulong*)pa, (ulong*)pb, (ulong*)pm, (ulong*)pr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Divide256(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!ZiskArith256Flag.IsActive)
        {
            UInt256.Divide(in a, in b, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        Unsafe.SkipInit(out UInt256 remainder);
        fixed (UInt256* pa = &a, pb = &b, pq = &result)
            Accelerators.DivRem256((ulong*)pa, (ulong*)pb, (ulong*)pq, (ulong*)&remainder);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial void Mod256(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!ZiskArith256Flag.IsActive)
        {
            UInt256.Mod(in a, in b, out result);
            return;
        }

        Unsafe.SkipInit(out result);
        fixed (UInt256* pa = &a, pb = &b, pr = &result)
            Accelerators.ReduceMod256((ulong*)pa, (ulong*)pb, (ulong*)pr);
    }

    /// <summary>OpenVM's 256-bit multiplication: the low 256 bits of <c>a * b</c> into <paramref name="result"/>.</summary>
    /// <remarks>
    /// One instruction of OpenVM's bigint extension, from <c>Nethermind.OpenVM.Runtime</c>. Every pointer must be 8-byte
    /// aligned. Both factors are read before the product is written, so <paramref name="result"/> may alias either.
    /// </remarks>
    [DllImport("__Internal", EntryPoint = "zkvm_u256_mul", ExactSpelling = true), SuppressGCTransition]
    internal static extern void OpenVmMultiply256(ulong* result, ulong* a, ulong* b);
}
