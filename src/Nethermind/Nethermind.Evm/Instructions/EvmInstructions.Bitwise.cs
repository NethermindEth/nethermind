// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

using Word = Vector256<byte>;

internal sealed partial class EvmInstructions
{
    public interface IOpBitwise
    {
        virtual static long GasCost => GasCostOf.VeryLow;
        abstract static Word Operation(Word a, Word b);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBitwise<TOpBitwise>(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpBitwise : struct, IOpBitwise
    {
        gasAvailable -= TOpBitwise.GasCost;

        ref byte bytesRef = ref stack.PopBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
        Word aVec = ReadUnaligned<Word>(ref bytesRef);

        // Peek the top ref to avoid pushing and popping
        bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
        Word bVec = ReadUnaligned<Word>(ref bytesRef);

        // Do not need to push as we peeked the last ref, so we can write directly to it
        WriteUnaligned(ref bytesRef, TOpBitwise.Operation(aVec, bVec));

        return EvmExceptionType.None;
    // Reduce inline code returns, also jump forward to be unpredicted by the branch predictor
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    public struct OpBitwiseAnd : IOpBitwise
    {
        public static Word Operation(Word a, Word b) => Vector256.BitwiseAnd(a, b);
    }

    public struct OpBitwiseOr : IOpBitwise
    {
        public static Word Operation(Word a, Word b) => Vector256.BitwiseOr(a, b);
    }

    public struct OpBitwiseXor : IOpBitwise
    {
        public static Word Operation(Word a, Word b) => Vector256.Xor(a, b);
    }

    public struct OpBitwiseEq : IOpBitwise
    {
        public static Word One = Vector256.Create(
            (byte)
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 1
        );

        public static Word Operation(Word a, Word b) => a == b ? One : default;
    }
}
