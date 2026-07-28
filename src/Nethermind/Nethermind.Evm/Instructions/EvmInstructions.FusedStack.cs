// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedDupBinaryCore(ref EvmStack stack, ulong operand)
    {
        int depth = (byte)operand;
        ref byte topRef = ref stack.PeekTopForDupBinaryUnchecked(depth, out EvmWord duplicate);

        return (Instruction)(byte)(operand >> 8) switch
        {
            Instruction.ADD => ApplyDupMath<OpAdd>(ref topRef, ref duplicate),
            Instruction.SUB => ApplyDupMath<OpSub>(ref topRef, ref duplicate),
            Instruction.MUL => ApplyDupMath<OpMul>(ref topRef, ref duplicate),
            Instruction.DIV => ApplyDupMath<OpDiv>(ref topRef, ref duplicate),
            Instruction.SDIV => ApplyDupMath<OpSDiv>(ref topRef, ref duplicate),
            Instruction.MOD => ApplyDupMath<OpMod>(ref topRef, ref duplicate),
            Instruction.SMOD => ApplyDupMath<OpSMod>(ref topRef, ref duplicate),
            Instruction.LT => ApplyDupMath<OpLt>(ref topRef, ref duplicate),
            Instruction.GT => ApplyDupMath<OpGt>(ref topRef, ref duplicate),
            Instruction.SLT => ApplyDupMath<OpSLt>(ref topRef, ref duplicate),
            Instruction.SGT => ApplyDupMath<OpSGt>(ref topRef, ref duplicate),
            Instruction.EQ => ApplyDupBitwise<OpBitwiseEq>(ref topRef, in duplicate),
            Instruction.AND => ApplyDupBitwise<OpBitwiseAnd>(ref topRef, in duplicate),
            Instruction.OR => ApplyDupBitwise<OpBitwiseOr>(ref topRef, in duplicate),
            Instruction.XOR => ApplyDupBitwise<OpBitwiseXor>(ref topRef, in duplicate),
            _ => EvmExceptionType.BadInstruction,
        };
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType FusedSwap1BinaryCore(ref EvmStack stack, Instruction instruction) =>
        instruction switch
        {
            Instruction.ADD => ApplySwapMath<OpAdd>(ref stack),
            Instruction.SUB => ApplySwapMath<OpSub>(ref stack),
            Instruction.MUL => ApplySwapMath<OpMul>(ref stack),
            Instruction.DIV => ApplySwapMath<OpDiv>(ref stack),
            Instruction.SDIV => ApplySwapMath<OpSDiv>(ref stack),
            Instruction.MOD => ApplySwapMath<OpMod>(ref stack),
            Instruction.SMOD => ApplySwapMath<OpSMod>(ref stack),
            Instruction.LT => ApplySwapMath<OpLt>(ref stack),
            Instruction.GT => ApplySwapMath<OpGt>(ref stack),
            Instruction.SLT => ApplySwapMath<OpSLt>(ref stack),
            Instruction.SGT => ApplySwapMath<OpSGt>(ref stack),
            Instruction.EQ => ApplySwapBitwise<OpBitwiseEq>(ref stack),
            Instruction.AND => ApplySwapBitwise<OpBitwiseAnd>(ref stack),
            Instruction.OR => ApplySwapBitwise<OpBitwiseOr>(ref stack),
            Instruction.XOR => ApplySwapBitwise<OpBitwiseXor>(ref stack),
            _ => EvmExceptionType.BadInstruction,
        };

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType ApplyDupMath<TOpMath>(ref byte topRef, ref EvmWord duplicate)
        where TOpMath : struct, IOpMath2Param
    {
        EvmStack.ReadUInt256FromSlot(ref As<EvmWord, byte>(ref duplicate), out Int256.UInt256 a);
        EvmStack.ReadUInt256FromSlot(ref topRef, out Int256.UInt256 b);
        TOpMath.Operation(in a, in b, out Int256.UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType ApplyDupBitwise<TOpBitwise>(ref byte topRef, in EvmWord duplicate)
        where TOpBitwise : struct, IOpBitwise
    {
        EvmWord top = ReadUnaligned<EvmWord>(ref topRef);
        WriteUnaligned(ref topRef, TOpBitwise.Operation(in duplicate, in top));
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType ApplySwapMath<TOpMath>(ref EvmStack stack)
        where TOpMath : struct, IOpMath2Param
    {
        ref byte secondRef = ref stack.Pop1Peek32BytesUnchecked(out Int256.UInt256 top);

        EvmStack.ReadUInt256FromSlot(ref secondRef, out Int256.UInt256 second);
        TOpMath.Operation(in second, in top, out Int256.UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref secondRef, in result);
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType ApplySwapBitwise<TOpBitwise>(ref EvmStack stack)
        where TOpBitwise : struct, IOpBitwise
    {
        ref byte secondRef = ref stack.Pop1PeekWordUnchecked(out EvmWord top);

        EvmWord second = ReadUnaligned<EvmWord>(ref secondRef);
        WriteUnaligned(ref secondRef, TOpBitwise.Operation(in second, in top));
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool TestDupAndIsZero(ref EvmStack stack, int depth)
    {
        ref byte topRef = ref stack.PeekTopForDupBinaryUnchecked(depth, out EvmWord duplicate);
        EvmWord top = ReadUnaligned<EvmWord>(ref topRef);
        stack.Head--;
        return (top & duplicate) == default;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool TestMaskIsZero(ref EvmStack stack, byte shift)
    {
        ref byte bSlot = ref stack.Pop1Peek32BytesUnchecked(out Int256.UInt256 a);
        EvmStack.ReadUInt256FromSlot(ref bSlot, out Int256.UInt256 b);
        stack.PopUnchecked();
        ref byte cSlot = ref stack.PeekBytesByRefUnchecked();
        EvmStack.ReadUInt256FromSlot(ref cSlot, out Int256.UInt256 c);
        stack.PopUnchecked();

        Int256.UInt256 shiftAmount = shift;
        OpShl.Operation(in shiftAmount, in a, out Int256.UInt256 shifted);
        OpSub.Operation(in shifted, in b, out Int256.UInt256 subtracted);
        return (subtracted & c).IsZero;
    }
}
