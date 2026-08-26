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
        ref byte topRef = ref stack.PeekTopForDupBinary(depth, out EvmWord duplicate, out EvmExceptionType exceptionType);
        if (exceptionType != EvmExceptionType.None)
            return exceptionType;

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
}
