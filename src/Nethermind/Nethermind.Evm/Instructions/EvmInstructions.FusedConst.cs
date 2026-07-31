// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.CompilerServices.Unsafe;
using Nethermind.Core;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Fused <c>PUSH const; binary-op</c>: runs against the pre-decoded constant on the stack top —
    /// no push/pop, one dispatch. Preserves per-op failure order: push overflow before op underflow.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedConstBinaryCore<TOpMath>(ref EvmStack stack, in UInt256 a)
        where TOpMath : struct, IOpMath2Param
    {
        if (stack.Head == EvmStack.MaxStackSize - 1)
            return EvmExceptionType.StackOverflow;

        ref byte topRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref topRef)) return EvmExceptionType.StackUnderflow;

        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpMath.Operation(in a, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        return EvmExceptionType.None;
    }

    /// <summary>Fused <c>PUSH shift-amount; SHL/SHR</c>, mirroring <see cref="ShiftCore{TOpShift, TTracingInst}"/>.
    /// The amount is an analysis-time constant, and generated code shifts almost exclusively by whole
    /// bytes (address and selector packing, fixed-point scaling), so the byte-aligned case runs as a
    /// byte move over the stack representation: no limb conversion and no shift arithmetic at all.
    /// Big-endian order makes SHL a move toward index zero and SHR a move away from it.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedConstShiftCore<TOpShift>(ref EvmStack stack, in UInt256 a)
        where TOpShift : struct, IOpShift
    {
        if (stack.Head == EvmStack.MaxStackSize - 1)
            return EvmExceptionType.StackOverflow;

        ref byte topRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref topRef)) return EvmExceptionType.StackUnderflow;

        // Mirrors ShiftCore: amounts of 256 or more shift everything out.
        if (!a.IsUint64 || a.u0 >= 256)
        {
            EvmStack.WriteUInt256ToSlot(ref topRef, in UInt256.Zero);
            return EvmExceptionType.None;
        }

        int amount = (int)a.u0;
        if ((amount & 7) == 0)
        {
            int bytes = amount >> 3;
            Span<byte> slot = MemoryMarshal.CreateSpan(ref topRef, EvmStack.WordSize);
            if (typeof(TOpShift) == typeof(OpShl))
            {
                slot.Slice(bytes).CopyTo(slot);
                slot.Slice(EvmStack.WordSize - bytes).Clear();
            }
            else
            {
                slot.Slice(0, EvmStack.WordSize - bytes).CopyTo(slot.Slice(bytes));
                slot.Slice(0, bytes).Clear();
            }

            return EvmExceptionType.None;
        }

        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpShift.Operation(in a, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Fused <c>POP; POP</c>. One bounds check for two drops: the depths this rejects are exactly the
    /// depths at which one of the two POPs would have underflowed, so the failure matches per-op
    /// interpretation. Gas is precharged by the block, as for any in-block entry.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType FusedPopPopCore(ref EvmStack stack)
    {
        if (stack.Head < 2) return EvmExceptionType.StackUnderflow;
        stack.Head -= 2;
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Fused <c>PUSH1 a; PUSH1 b</c>, with both immediates packed into the entry operand: <c>a</c> in
    /// the low byte, <c>b</c> in the next. The leading check rejects only a full stack; at one slot
    /// free the first push lands and the second one's own bound reports the overflow, exactly like
    /// the unfused pair. The intermediate write is unobservable — an exceptional halt discards it.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType FusedPush1Push1Core(ref EvmStack stack, ulong packed)
    {
        if (stack.Head > EvmStack.MaxStackSize - 2) return EvmExceptionType.StackOverflow;
        EvmExceptionType result = stack.PushUInt64<OffFlag>(packed & 0xFF);
        if (result != EvmExceptionType.None) return result;
        return stack.PushUInt64<OffFlag>((packed >> 8) & 0xFF);
    }

    /// <summary>
    /// Fused <c>PUSH const; bitwise-op</c> over the stack-representation pool: one vector load per
    /// operand, no limb conversion.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedConstBitwiseCore<TOpBitwise>(ref EvmStack stack, ref byte constantSlot)
        where TOpBitwise : struct, IOpBitwise
    {
        if (stack.Head == EvmStack.MaxStackSize - 1)
            return EvmExceptionType.StackOverflow;

        ref byte topRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref topRef)) return EvmExceptionType.StackUnderflow;

        EvmWord a = ReadUnaligned<EvmWord>(ref constantSlot);
        EvmWord b = ReadUnaligned<EvmWord>(ref topRef);
        WriteUnaligned(ref topRef, TOpBitwise.Operation(in a, in b));
        return EvmExceptionType.None;
    }
}
