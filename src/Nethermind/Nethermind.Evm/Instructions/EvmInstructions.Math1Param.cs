// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

using Word = Vector256<byte>;

internal sealed partial class EvmInstructions
{
    public interface IOpMath1Param
    {
        virtual static long GasCost => GasCostOf.VeryLow;
        abstract static Word Operation(Word value);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath1Param<TOpMath>(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpMath : struct, IOpMath1Param
    {
        gasAvailable -= TOpMath.GasCost;

        // Peek the top ref to avoid pushing and popping
        ref byte bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;

        Word result = TOpMath.Operation(ReadUnaligned<Word>(ref bytesRef));

        // Do not need to push as we peeked the last ref, so we can write directly to it
        WriteUnaligned(ref bytesRef, result);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    public struct OpNot : IOpMath1Param
    {
        public static Word Operation(Word value) => Vector256.OnesComplement(value);
    }

    public struct OpIsZero : IOpMath1Param
    {
        public static Word Operation(Word value) => value == default ? OpBitwiseEq.One : default;
    }
}
