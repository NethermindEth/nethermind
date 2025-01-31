// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

internal sealed partial class EvmInstructions
{
    public interface IOpBitwise
    {
        virtual static long GasCost => GasCostOf.VeryLow;
        abstract static Vector256<byte> Operation(Vector256<byte> a, Vector256<byte> b);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionBitwise<TOpBitwise>(IEvm _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpBitwise : struct, IOpBitwise
    {
        gasAvailable -= TOpBitwise.GasCost;

        ref byte bytesRef = ref stack.PopBytesByRef();
        if (IsNullRef(ref bytesRef)) return EvmExceptionType.StackUnderflow;
        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

        bytesRef = ref stack.PopBytesByRef();
        if (IsNullRef(ref bytesRef)) return EvmExceptionType.StackUnderflow;
        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

        WriteUnaligned(ref stack.PushBytesRef(), TOpBitwise.Operation(aVec, bVec));

        return EvmExceptionType.None;
    }

    public struct OpBitwiseAnd : IOpBitwise
    {
        public static Vector256<byte> Operation(Vector256<byte> a, Vector256<byte> b) => Vector256.BitwiseAnd(a, b);
    }

    public struct OpBitwiseOr : IOpBitwise
    {
        public static Vector256<byte> Operation(Vector256<byte> a, Vector256<byte> b) => Vector256.BitwiseOr(a, b);
    }

    public struct OpBitwiseXor : IOpBitwise
    {
        public static Vector256<byte> Operation(Vector256<byte> a, Vector256<byte> b) => Vector256.Xor(a, b);
    }

    public struct OpBitwiseEq : IOpBitwise
    {
        public static Vector256<byte> One = Vector256.Create(
            (byte)
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 1
        );

        public static Vector256<byte> Operation(Vector256<byte> a, Vector256<byte> b) => a == b ? One : default;
    }
}
