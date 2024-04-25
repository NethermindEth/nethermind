// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

internal sealed partial class EvmInstructions
{
    public interface IOpMath1Param
    {
        virtual static long GasCost => GasCostOf.VeryLow;
        abstract static Vector256<byte> Operation(ref byte bytesRef);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath1Param<TOpMath, TTracingInstructions>(ref EvmStack<TTracingInstructions> stack, ref long gasAvailable)
        where TOpMath : struct, IOpMath1Param
        where TTracingInstructions : struct, IIsTracing
    {
        gasAvailable -= TOpMath.GasCost;

        ref byte bytesRef = ref stack.PopBytesByRef();
        if (IsNullRef(ref bytesRef)) return EvmExceptionType.StackUnderflow;

        Vector256<byte> result = TOpMath.Operation(ref bytesRef);

        WriteUnaligned(ref stack.PushBytesRef(), result);

        return EvmExceptionType.None;
    }

    public struct OpNot : IOpMath1Param
    {
        public static Vector256<byte> Operation(ref byte bytesRef) => Vector256.OnesComplement(ReadUnaligned<Vector256<byte>>(ref bytesRef));
    }

    public struct OpIsZero : IOpMath1Param
    {
        public static Vector256<byte> Operation(ref byte bytesRef) => As<byte, Vector256<byte>>(ref bytesRef) == default ? OpBitwiseEq.One : default;
    }
}
