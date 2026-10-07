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
            if (!OpenVmMulModHintFlag.IsActive || a >= m || b >= m || !TryMulModHint(in a, in b, in m, out result))
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

    /// <summary>Takes <c>(a * b) mod m</c> from the runner, for <paramref name="a"/> and <paramref name="b"/> below <paramref name="m"/>.</summary>
    /// <returns>Whether the runner's quotient and remainder passed the check; if not, the caller computes the result.</returns>
    /// <remarks>
    /// Both factors below <paramref name="m"/> keep the quotient of their 512-bit product below 2^256. The runner's
    /// pair is accepted only if <c>q * m + r</c> is the product exactly and <c>r &lt; m</c>, which pins both.
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryMulModHint(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        ulong* product = stackalloc ulong[4 * LimbsPerWord];
        ulong* quotient = product + 2 * LimbsPerWord;
        ulong* remainder = quotient + LimbsPerWord;
        fixed (UInt256* pa = &a, pb = &b, pm = &m)
        {
            OpenVmMultiply512((ulong*)pa, (ulong*)pb, product);
            OpenVmDivRemHint(product, (ulong*)pm, quotient);
            if (OpenVmIsDivRem(product, (ulong*)pm, quotient, remainder) == 0)
            {
                Unsafe.SkipInit(out result);
                return false;
            }
        }

        result = *(UInt256*)remainder;
        return true;
    }

    private const int LimbsPerWord = 4;

    [DllImport("__Internal", EntryPoint = "openvm_mul_512", ExactSpelling = true), SuppressGCTransition]
    private static extern void OpenVmMultiply512(ulong* x, ulong* y, ulong* product);

    [DllImport("__Internal", EntryPoint = "openvm_divrem_hint", ExactSpelling = true), SuppressGCTransition]
    private static extern void OpenVmDivRemHint(ulong* numerator, ulong* divisor, ulong* quotientRemainder);

    [DllImport("__Internal", EntryPoint = "openvm_is_divrem", ExactSpelling = true), SuppressGCTransition]
    private static extern int OpenVmIsDivRem(ulong* numerator, ulong* divisor, ulong* quotient, ulong* remainder);
}
