// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    public interface IOpMath3Param : IGasCost
    {
        static ulong IGasCost.GasCost => GasCostOf.Mid;
        abstract static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath3Param<TGasPolicy, TOpMath, TTracingInst>(VirtualMachine<TGasPolicy> _, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath3Param
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume<TOpMath>(ref gas);

        // Pop a and b, peek the third slot for in-place write; skips the push overflow check.
        ref byte topRef = ref stack.Pop2Peek32Bytes(out UInt256 a, out UInt256 b, out bool ok);
        if (!ok) goto StackUnderflow;

        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 c);
        if (c.IsZero)
        {
            // c-slot already held c; overwrite with zero (matches PushZero semantics).
            Unsafe.As<byte, Vector256<byte>>(ref topRef) = default;
        }
        else
        {
            TOpMath.Operation(in a, in b, in c, out UInt256 result);
            EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        }

        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
    StackUnderflow:
        // Jump forward to be unpredicted by the branch predictor
        return EvmExceptionType.StackUnderflow;
    }

    public struct OpAddMod : IOpMath3Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result) => UInt256.AddMod(in a, in b, in c, out result);
    }

    public struct OpMulMod : IOpMath3Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, in UInt256 c, out UInt256 result)
        {
            // Fixed-point helpers take the 512-bit product of a multiplication by asking for
            // a*b mod (2^256-1) alongside the truncated low half, so this modulus carries
            // essentially all of the MULMOD traffic on pool-heavy calls. It also happens to be the
            // one modulus that needs no division.
            if (c == UInt256.MaxValue)
            {
                MultiplyModMaxValue(in a, in b, out result);
                return;
            }

            UInt256.MultiplyMod(in a, in b, in c, out result);
        }
    }

    /// <summary>
    /// <c>a*b mod (2^256-1)</c> by folding the product instead of dividing it. Because
    /// 2^256 is congruent to 1 for this modulus, the product's high half contributes its own value
    /// and a dropped carry contributes one, so the result is the sum of the halves plus the carry.
    /// The sum of two residues needs at most one reduction, and it can only ever land exactly on the
    /// modulus - the halves sum to at most 2*(2^256-1), which cannot reach 2^257-1 - so the single
    /// equality check below is the whole reduction.
    /// </summary>
    private static void MultiplyModMaxValue(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        Multiply512(in a, in b, out UInt256 low, out UInt256 high);
        if (UInt256.AddOverflow(in low, in high, out UInt256 sum))
        {
            UInt256.Add(in sum, in UInt256.One, out sum);
        }

        result = sum == UInt256.MaxValue ? UInt256.Zero : sum;
    }

    /// <summary>
    /// Schoolbook 4x4 limb product, unrolled so every intermediate stays in a register. Each partial
    /// sum fits a 128-bit accumulator: the widest term is a full limb product plus two limbs, which
    /// stays below 2^128.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Multiply512(in UInt256 a, in UInt256 b, out UInt256 low, out UInt256 high)
    {
        ulong a0 = a.u0, a1 = a.u1, a2 = a.u2, a3 = a.u3;
        ulong b0 = b.u0, b1 = b.u1, b2 = b.u2, b3 = b.u3;

        UInt128 t = (UInt128)a0 * b0; ulong r0 = (ulong)t; ulong carry = (ulong)(t >> 64);
        t = (UInt128)a0 * b1 + carry; ulong r1 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a0 * b2 + carry; ulong r2 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a0 * b3 + carry; ulong r3 = (ulong)t; ulong r4 = (ulong)(t >> 64);

        t = (UInt128)a1 * b0 + r1; r1 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a1 * b1 + r2 + carry; r2 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a1 * b2 + r3 + carry; r3 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a1 * b3 + r4 + carry; r4 = (ulong)t; ulong r5 = (ulong)(t >> 64);

        t = (UInt128)a2 * b0 + r2; r2 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a2 * b1 + r3 + carry; r3 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a2 * b2 + r4 + carry; r4 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a2 * b3 + r5 + carry; r5 = (ulong)t; ulong r6 = (ulong)(t >> 64);

        t = (UInt128)a3 * b0 + r3; r3 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a3 * b1 + r4 + carry; r4 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a3 * b2 + r5 + carry; r5 = (ulong)t; carry = (ulong)(t >> 64);
        t = (UInt128)a3 * b3 + r6 + carry; r6 = (ulong)t; ulong r7 = (ulong)(t >> 64);

        low = new UInt256(r0, r1, r2, r3);
        high = new UInt256(r4, r5, r6, r7);
    }
}
